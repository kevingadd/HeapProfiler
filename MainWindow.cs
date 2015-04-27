﻿/*
The contents of this file are subject to the Mozilla Public License
Version 1.1 (the "License"); you may not use this file except in
compliance with the License. You may obtain a copy of the License at
http://www.mozilla.org/MPL/

Software distributed under the License is distributed on an "AS IS"
basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See the
License for the specific language governing rights and limitations
under the License.

The Original Code is Windows Heap Profiler Frontend.

The Initial Developer of the Original Code is Mozilla Corporation.

Original Author: K. Gadd (kg@luminance.org)
*/

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.IO;
using Squared.Task;
using Squared.Util;
using Squared.Util.Bind;
using Microsoft.Win32;

namespace HeapProfiler {
    public partial class MainWindow : TaskForm {
        public const int CaptureMemoryChangeThresholdPercentage = 5;
        public const double CaptureCheckIntervalSeconds = 1.0;
        public const double CaptureMaxIntervalSeconds = 60.0;

        public HeapRecording Instance = null;

        protected HashSet<string> KnownFunctionNames = new HashSet<string>();
        protected string CurrentFilter = null, PendingFilter = null;
        protected Dictionary<HeapSnapshotInfo, FilteredHeapSnapshotInfo> CurrentFilterData = null;
        protected IFuture PendingFilterFuture = null;

        protected IFuture AutoCaptureFuture = null;
        protected bool WasMinimized = false;

        readonly IBoundMember[] PersistedControls;

        public MainWindow (TaskScheduler scheduler) 
            : base(scheduler) {
            InitializeComponent();

            PersistedControls = new[] {
                BoundMember.New(() => ExecutablePath.Text),
                BoundMember.New(() => Arguments.Text),
                BoundMember.New(() => WorkingDirectory.Text)
            };

            SnapshotTimeline.ItemValueGetter = GetPagedMemory;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;

            LoadPersistedValues();

            Program.ErrorList.ErrorReported += UpdateErrorCount;
            Program.ErrorList.ListCleared += UpdateErrorCount;
        }

        protected void UpdateErrorCount (object sender, EventArgs e) {
            ErrorDialogMenu.Text = String.Format("&Errors ({0})", Program.ErrorList.Count);
        }

        protected string ChooseName (IBoundMember bm) {
            return String.Format("{0}_{1}", (bm.Target as Control).Name, bm.Name);
        }

        protected void LoadPersistedValues () {
            if (!Registry.CurrentUser.SubKeyExists("Software\\HeapProfiler"))
                return;

            using (var key = Registry.CurrentUser.OpenSubKey("Software\\HeapProfiler"))
            foreach (var pc in PersistedControls)
                pc.Value = key.GetValue(ChooseName(pc), pc.Value);
        }

        protected void SavePersistedValues () {
            using (var key = Registry.CurrentUser.OpenOrCreateSubKey("Software\\HeapProfiler"))
            foreach (var pc in PersistedControls)
                key.SetValue(ChooseName(pc), pc.Value);
        }

        private void SelectExecutable_Click (object sender, EventArgs e) {
            using (var dialog = new OpenFileDialog()) {
                dialog.Title = "Select Executable";
                dialog.FileName = ExecutablePath.Text;
                dialog.ShowReadOnly = false;
                dialog.ValidateNames = true;
                dialog.CheckFileExists = true;
                dialog.AddExtension = false;
                dialog.Filter = "Executables|*.exe";
                dialog.DereferenceLinks = true;

                if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK)
                    return;

                ExecutablePath.Text = dialog.FileName;
            }
        }

        private void ExecutablePath_DragOver (object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Link;
        }

        private void ExecutablePath_DragDrop (object sender, DragEventArgs e) {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null)
                return;

            ExecutablePath.Text = files[0];
        }

        private void DisposeInstance () {
            if (Instance != null)
                Instance.Dispose();

            if (PendingFilterFuture != null)
                PendingFilterFuture.Dispose();

            PendingFilter = CurrentFilter = null;
            CurrentFilterData = null;

            HeapFilter.Filter = null;
        }

        private void LaunchProcess_Click (object sender, EventArgs e) {
            DisposeInstance();

            LaunchProcess.Enabled = false;

            Instance = HeapRecording.StartProcess(
                Scheduler, Activities,
                ExecutablePath.Text,
                Arguments.Text,
                WorkingDirectory.Text
            );
            SubscribeToEvents(Instance);

            RefreshStatus();
            RefreshSnapshots();

            if (AutoCapture.Checked)
                AutoCaptureFuture = Start(AutoCaptureTask());
        }

        private void ExecutablePath_TextChanged (object sender, EventArgs e) {
            RefreshLaunchEnabled();
        }

        private void RefreshLaunchEnabled () {
            bool enabled = true;

            try {
                var path = Path.GetFullPath(ExecutablePath.Text);
                if (!File.Exists(path))
                    enabled = false;
                if (Path.GetExtension(path).ToLowerInvariant() != ".exe")
                    enabled = false;
            } catch {
                enabled = false;
            }

            if (Instance != null && Instance.Running)
                enabled = false;

            LaunchProcess.Enabled = enabled;
        }

        private void RefreshStatus () {
            ExecutableStatus.Text = String.Format(
                "Status: {0}", 
                (Instance != null) ? (Instance.Running ? "Running" : "Exited") : "Not Started"
            );

            bool running = (Instance != null) && (Instance.Running);
            CaptureSnapshot.Enabled = running;

            if ((!running) && (AutoCaptureFuture != null)) {
                AutoCaptureFuture.Dispose();
                AutoCaptureFuture = null;
            }

            RefreshLaunchEnabled();
        }

        private void RefreshSnapshots () {
            SnapshotTimeline.Items = Instance.Snapshots;
            SnapshotTimeline.Invalidate();

            DiffSelection.Enabled = SnapshotTimeline.HasSelection &&
                (SnapshotTimeline.Selection.First != SnapshotTimeline.Selection.Second);
            ViewSelection.Enabled = SnapshotTimeline.HasSelection &&
                (SnapshotTimeline.Selection.First == SnapshotTimeline.Selection.Second);
        }

        private void CaptureSnapshot_Click (object sender, EventArgs e) {
            AutoCapture.Enabled = CaptureSnapshot.Enabled = false;
            Instance.CaptureSnapshot()
                .RegisterOnComplete((_) => {
                    AutoCapture.Enabled = CaptureSnapshot.Enabled = true;
                });
        }

        private void MainWindow_FormClosing (object sender, FormClosingEventArgs e) {
            SavePersistedValues();

            if (Instance != null)
                Instance.Dispose();
        }

        private void DiffSelection_Click (object sender, EventArgs e) {
            var indices = SnapshotTimeline.Selection;

            ShowDiff(indices.First, indices.Second);
        }

        protected void ShowDiff (int index1, int index2) {
            var viewer = new DiffViewer(Scheduler, Instance);

            viewer.Start(viewer.LoadRange(Pair.New(index1, index2)));

            viewer.Show(this);
        }

        private void MainWindow_FormClosed (object sender, FormClosedEventArgs e) {
            Application.Exit();
        }

        private void ExitMenu_Click (object sender, EventArgs e) {
            Application.Exit();
        }

        private void SymbolPathMenu_Click (object sender, EventArgs e) {
            using (var dialog = new SymbolSettingsDialog())
                dialog.ShowDialog(this);
        }

        private void OpenFilesMenu_Click (object sender, EventArgs e) {
            using (var dialog = new OpenFileDialog()) {
                dialog.Filter = "Heap Files|*.heaprecording;*.heapdiff|Heap Recordings|*.heaprecording|Heap Diffs|*.heapdiff";
                dialog.CheckFileExists = true;
                dialog.CheckPathExists = true;
                dialog.Multiselect = true;
                dialog.ShowReadOnly = false;
                dialog.Title = "Open";

                if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK)
                    return;

                Scheduler.Start(
                    Program.OpenFilenames(dialog.FileNames, this),
                    TaskExecutionPolicy.RunAsBackgroundTask
                );
            }
        }

        private void SelectWorkingDirectory_Click (object sender, EventArgs e) {
            using (var dialog = new FolderBrowserDialog()) {
                dialog.Description = "Select Working Directory";

                if (Directory.Exists(WorkingDirectory.Text))
                    dialog.SelectedPath = WorkingDirectory.Text;
                else if (File.Exists(ExecutablePath.Text))
                    dialog.SelectedPath = Path.GetDirectoryName(ExecutablePath.Text);

                if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK)
                    return;

                WorkingDirectory.Text = dialog.SelectedPath;
            }
        }

        private void WorkingDirectory_DragDrop (object sender, DragEventArgs e) {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null)
                return;

            WorkingDirectory.Text = Path.GetDirectoryName(files[0]);
        }

        private void WorkingDirectory_DragOver (object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Link;
        }

        private void AutoCapture_CheckedChanged (object sender, EventArgs e) {
            if (AutoCapture.Checked && CaptureSnapshot.Enabled) {
                AutoCaptureFuture = Start(AutoCaptureTask());
            } else if (AutoCaptureFuture != null) {
                AutoCaptureFuture.Dispose();
                AutoCaptureFuture = null;
            }
        }

        protected IEnumerator<object> AutoCaptureTask () {
            var sleep = new Sleep(0.1);

            while ((Instance == null) || (!Instance.Running))
                yield return sleep;

            sleep = new Sleep(CaptureCheckIntervalSeconds);

            const long captureInterval = (long)(CaptureMaxIntervalSeconds * Time.SecondInTicks);
            long lastPaged = 0, lastWorking = 0, lastCaptureWhen = 0;
            bool shouldCapture;

            while (AutoCapture.Checked && Instance.Running) {
                Instance.Process.Refresh();
                var pagedDelta = Math.Abs(Instance.Process.PagedMemorySize64 - lastPaged);
                var workingDelta = Math.Abs(Instance.Process.WorkingSet64 - lastWorking);
                var deltaPercent = Math.Max(
                    pagedDelta * 100 / Math.Max(Instance.Process.PagedMemorySize64, lastPaged),
                    workingDelta * 100 / Math.Max(Instance.Process.WorkingSet64, lastWorking)
                );
                var elapsed = Time.Ticks - lastCaptureWhen;

                shouldCapture = (deltaPercent >= CaptureMemoryChangeThresholdPercentage) || 
                    (elapsed > captureInterval);

                if (shouldCapture) {
                    lastPaged = Instance.Process.PagedMemorySize64;
                    lastWorking = Instance.Process.WorkingSet64;
                    lastCaptureWhen = Time.Ticks;
                    yield return Instance.CaptureSnapshot();
                }

                yield return sleep;
            }
        }

        private void SaveAllSnapshots_Click (object sender, EventArgs e) {
            if (Instance == null)
                return;
            if (Instance.Snapshots.Count == 0)
                return;

            using (var dialog = new FolderBrowserDialog()) {
                dialog.Description = "Save snapshots to folder";
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK)
                    return;

                using (Activities.AddItem("Saving snapshots"))
                foreach (var snap in Instance.Snapshots) {
                    var destPath = Path.Combine(
                        dialog.SelectedPath,
                        String.Format(
                            "{0:0000}_{1}.heapsnap", 
                            snap.Index, 
                            snap.Timestamp.ToString("u").Replace(":", "_")
                        )
                    );
                    try {
                        File.Copy(snap.Filename, destPath, true);
                    } catch (Exception ex) {
                        MessageBox.Show("Save failed: " + ex.ToString());
                        return;
                    }
                }
            }
        }

        private void OpenSnapshotsMenu_Click (object sender, EventArgs e) {
            using (var dialog = new OpenFileDialog()) {
                dialog.Title = "Open Snapshots";
                dialog.Filter = "Saved Snapshots|*.heapsnap";
                dialog.Multiselect = true;
                dialog.ShowReadOnly = false;
                dialog.AddExtension = false;
                dialog.CheckFileExists = true;

                if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK)
                    return;

                OpenSnapshots(dialog.FileNames);
            }
        }

        protected void SubscribeToEvents (HeapRecording instance) {
            Instance.StatusChanged += (s, _) => RefreshStatus();
            Instance.SnapshotsChanged += (s, _) => RefreshSnapshots();
            Instance.SymbolsChanged += (s, _) => {
                Scheduler.Start(
                    RefreshFunctionNames(Instance), TaskExecutionPolicy.RunAsBackgroundTask
                );
            };
        }

        public void OpenRecording (string filename) {
            if (!DatabaseFile.CheckTokenFileVersion(filename)) {
                MessageBox.Show(this, "The recording you have selected was produced by a different version of Heap Profiler and cannot be opened.", "Error");
                return;
            }

            DisposeInstance();

            Instance = HeapRecording.FromRecording(
                Scheduler, Activities, filename
            );
            SubscribeToEvents(Instance);

            RefreshStatus();
            RefreshSnapshots();

            Scheduler.Start(
                RefreshFunctionNames(Instance), TaskExecutionPolicy.RunAsBackgroundTask
            );
        }

        public void OpenSnapshots (IEnumerable<string> filenames) {
            DisposeInstance();

            Instance = HeapRecording.FromSnapshots(
                Scheduler, Activities, filenames.OrderBy((f) => f)
            );
            SubscribeToEvents(Instance);

            RefreshStatus();
            RefreshSnapshots();
        }

        private void Activities_PreferredSizeChanged (object sender, EventArgs e) {
            if (WindowState == FormWindowState.Minimized)
                return;

            var margin = GroupSnapshots.Left;
            var ps = Activities.GetPreferredSize(new Size(
                GroupSnapshots.Width, ClientSize.Height
            ));

            int newTop = ClientSize.Height - ps.Height - margin;
            var newHeight = newTop - (ps.Height > 0 ? margin : 0) - GroupSnapshots.Top;

            if ((newTop == Activities.Top) && (newHeight == GroupSnapshots.Height))
                return;

            SuspendLayout();

            GroupSnapshots.SetBounds(
                GroupSnapshots.Left, GroupSnapshots.Top,
                GroupSnapshots.Width, newHeight
            );
            Activities.SetBounds(
                GroupSnapshots.Left, newTop, GroupSnapshots.Width, ps.Height
            );

            ResumeLayout(true);
        }

        private void SnapshotTimeline_SelectionChanged (object sender, EventArgs e) {
            DiffSelection.Enabled = SnapshotTimeline.HasSelection && 
                (SnapshotTimeline.Selection.First != SnapshotTimeline.Selection.Second);
            ViewSelection.Enabled = SnapshotTimeline.HasSelection &&
                (SnapshotTimeline.Selection.First == SnapshotTimeline.Selection.Second);
        }

        public static long GetPagedMemory (HeapSnapshotInfo item) {
            return item.Memory.Paged;
        }

        public static long GetVirtualMemory (HeapSnapshotInfo item) {
            return item.Memory.Virtual;
        }

        public static long GetWorkingSet (HeapSnapshotInfo item) {
            return item.Memory.WorkingSet;
        }

        public static long GetLargestFreeHeapBlock (HeapSnapshotInfo item) {
            return item.LargestFreeHeapBlock;
        }

        public static long GetAverageFreeHeapBlockSize (HeapSnapshotInfo item) {
            return item.AverageFreeBlockSize;
        }

        public static long GetLargestOccupiedHeapBlock (HeapSnapshotInfo item) {
            return item.LargestOccupiedHeapBlock;
        }

        public static long GetAverageOccupiedHeapBlockSize (HeapSnapshotInfo item) {
            return item.AverageOccupiedBlockSize;
        }

        public static long GetHeapFragmentation (HeapSnapshotInfo item) {
            return (long)(item.HeapFragmentation * 10000);
        }

        public long GetAllocationCount (HeapSnapshotInfo item) {
            FilteredHeapSnapshotInfo info = item;

            if (CurrentFilterData != null)
                if (!CurrentFilterData.TryGetValue(item, out info))
                    info = item;

            return (long)(info.AllocationCount);
        }

        public long GetBytesAllocated (HeapSnapshotInfo item) {
            FilteredHeapSnapshotInfo info = item;

            if (CurrentFilterData != null)
                if (!CurrentFilterData.TryGetValue(item, out info))
                    info = item;

            return (long)(info.BytesAllocated);
        }

        public long GetBytesOverhead (HeapSnapshotInfo item) {
            FilteredHeapSnapshotInfo info = item;

            if (CurrentFilterData != null)
                if (!CurrentFilterData.TryGetValue(item, out info))
                    info = item;

            return (long)(info.BytesOverhead);
        }

        public long GetBytesTotal (HeapSnapshotInfo item) {
            FilteredHeapSnapshotInfo info = item;

            if (CurrentFilterData != null)
                if (!CurrentFilterData.TryGetValue(item, out info))
                    info = item;

            return (long)(info.BytesTotal);
        }

        public static string FormatSizeBytes (long bytes) {
            return FileSize.Format(bytes);
        }

        public static string FormatPercentage (long percentage) {
            return String.Format("{0}%", (percentage / 100.0f));
        }

        public static string FormatCount (long count) {
            if (count < 1000)
                return count.ToString();
            else if (count < 1000000)
                return String.Format("{0:###,000}", count);
            else if (count < 1000000000)
                return String.Format("{0:###,000,000}", count);
            else
                return String.Format("{0:###,000,000,000}", count);
        }

        private void ViewPagedMemoryMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetPagedMemory;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewVirtualMemoryMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetVirtualMemory;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewWorkingSetMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetWorkingSet;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewLargestFreeHeapMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetLargestFreeHeapBlock;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewLargestOccupiedHeapMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetLargestOccupiedHeapBlock;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewAverageHeapBlockSizeMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetAverageOccupiedHeapBlockSize;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewAverageFreeBlockSizeMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetAverageFreeHeapBlockSize;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewHeapFragmentationMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetHeapFragmentation;
            SnapshotTimeline.ItemValueFormatter = FormatPercentage;
        }

        private void ViewBytesAllocatedMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetBytesAllocated;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewBytesOverheadMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetBytesOverhead;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewBytesAllocatedPlusOverheadMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetBytesTotal;
            SnapshotTimeline.ItemValueFormatter = FormatSizeBytes;
        }

        private void ViewAllocationCountMenu_Click (object sender, EventArgs e) {
            SnapshotTimeline.ItemValueGetter = GetAllocationCount;
            SnapshotTimeline.ItemValueFormatter = FormatCount;
        }

        private void SnapshotTimeline_ItemValueGetterChanged (object sender, EventArgs e) {
            var getter = SnapshotTimeline.ItemValueGetter;
            ViewPagedMemoryMenu.Checked = (getter == GetPagedMemory);
            ViewVirtualMemoryMenu.Checked = (getter == GetVirtualMemory);
            ViewWorkingSetMenu.Checked = (getter == GetWorkingSet);
            ViewLargestFreeHeapMenu.Checked = (getter == GetLargestFreeHeapBlock);
            ViewAverageFreeBlockSizeMenu.Checked = (getter == GetAverageFreeHeapBlockSize);
            ViewLargestOccupiedHeapMenu.Checked = (getter == GetLargestOccupiedHeapBlock);
            ViewAverageHeapBlockSizeMenu.Checked = (getter == GetAverageOccupiedHeapBlockSize);
            ViewHeapFragmentationMenu.Checked = (getter == GetHeapFragmentation);
            ViewAllocationCountMenu.Checked = (getter == GetAllocationCount);
            ViewBytesAllocatedMenu.Checked = (getter == GetBytesAllocated);
            ViewBytesOverheadMenu.Checked = (getter == GetBytesOverhead);
            ViewBytesAllocatedPlusOverheadMenu.Checked = (getter == GetBytesTotal);
        }

        private void ViewSelection_Click (object sender, EventArgs e) {
            var index = SnapshotTimeline.Selection.First;
            var viewer = new HeapViewer(Scheduler, Instance);
            viewer.SetSnapshot(index);
            viewer.Show(this);
        }

        private void SaveAsMenu_Click (object sender, EventArgs e) {
            using (var dialog = new FolderBrowserDialog()) {
                dialog.SelectedPath = Instance.Database.Storage.Folder;
                dialog.Description = "Save Heap Recording";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK)
                    return;

                if (dialog.SelectedPath == Instance.Database.Storage.Folder)
                    return;

                if (Directory.GetFiles(dialog.SelectedPath).Length > 0) {
                    if (MessageBox.Show("The folder you have selected already contains files. If you continue, they may be overwritten or deleted.", "Warning", MessageBoxButtons.OKCancel) == System.Windows.Forms.DialogResult.Cancel)
                        return;
                }

                Scheduler.Start(
                    SaveInstanceAs(dialog.SelectedPath), 
                    TaskExecutionPolicy.RunAsBackgroundTask
                );
            }
        }

        private IEnumerator<object> SaveInstanceAs (string targetFilename) {
            UseWaitCursor = true;
            Enabled = false;

            try {
                yield return Instance.SaveAs(targetFilename);
            } finally {
                UseWaitCursor = false;
                Enabled = true;
            }
        }

        private void MainWindow_SizeChanged (object sender, EventArgs e) {
            if (WindowState == FormWindowState.Minimized) {
                WasMinimized = true;
            } else if (WasMinimized) {
                WasMinimized = false;
                Activities_PreferredSizeChanged(Activities, EventArgs.Empty);
            }
        }

        private void OptionsMenu_DropDownOpening (object sender, EventArgs e) {
            AssociateDiffsMenu.Checked = HeapDiffAssociation.IsAssociated;
            AssociateRecordingsMenu.Checked = HeapRecordingAssociation.IsAssociated;
            AssociateSnapshotsMenu.Checked = HeapSnapshotAssociation.IsAssociated;
        }

        protected FileAssociation HeapDiffAssociation {
            get {
                var executablePath = Application.ExecutablePath;

                return new FileAssociation(
                    ".heapdiff", "HeapProfiler_heapdiff",
                    "Heap Profiler Diff",
                    String.Format("{0},0", executablePath),
                    String.Format("{0} \"%1\"", executablePath)
                );
            }
        }

        private void AssociateDiffsMenu_Click (object sender, EventArgs e) {
            var assoc = HeapDiffAssociation;
            assoc.IsAssociated = !assoc.IsAssociated;
        }

        protected FileAssociation HeapSnapshotAssociation {
            get {
                var executablePath = Application.ExecutablePath;

                return new FileAssociation(
                    ".heapsnap", "HeapProfiler_heapsnapshot",
                    "Heap Profiler Snapshot",
                    String.Format("{0},0", executablePath),
                    String.Format("{0} \"%1\"", executablePath)
                );
            }
        }

        private void AssociateSnapshotsMenu_Click (object sender, EventArgs e) {
            var assoc = HeapSnapshotAssociation;
            assoc.IsAssociated = !assoc.IsAssociated;
        }

        protected FileAssociation HeapRecordingAssociation {
            get {
                var executablePath = Application.ExecutablePath;

                return new FileAssociation(
                    ".heaprecording", "HeapProfiler_heaprecording",
                    "Heap Profiler Recording",
                    String.Format("{0},0", executablePath),
                    String.Format("{0} \"%1\"", executablePath)
                );
            }
        }

        private void AssociateRecordingsMenu_Click (object sender, EventArgs e) {
            var assoc = HeapRecordingAssociation;
            assoc.IsAssociated = !assoc.IsAssociated;
        }

        private void MainWindow_DragDrop (object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
                var filenames = (string[])e.Data.GetData(DataFormats.FileDrop);

                Scheduler.Start(
                    Program.OpenFilenames(filenames, this), 
                    TaskExecutionPolicy.RunAsBackgroundTask
                );
            }
        }

        private void MainWindow_DragOver (object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        protected IEnumerator<object> RefreshFunctionNames (HeapRecording instance) {
            var sleep = new Sleep(0.05);
            // This sucks :(
            while (instance.Database.SymbolsByFunction == null)
                yield return sleep;

            var result = new HashSet<string>();

            var keys = instance.Database.SymbolsByFunction.GetAllKeys();
            using (keys)
                yield return keys;

            foreach (var key in keys.Result) {
                var text = key.Value as string;

                if (text != null)
                    result.Add(text);
            }

            KnownFunctionNames = result;
            HeapFilter.AutoCompleteItems = result;
        }

        protected IEnumerator<object> FilterHeapData (HeapRecording instance, string filter) {
            var result = new Dictionary<HeapSnapshotInfo, FilteredHeapSnapshotInfo>();

            var regex = MainWindow.FilterToRegex(filter);
            var functionNames = (from functionName in KnownFunctionNames
                                 where regex.IsMatch(functionName)
                                 select functionName).Distinct();

            using (var activity = Activities.AddItem("Filtering heap")) {
                var fFrameIDs = instance.Database.SymbolsByFunction.Find(functionNames);
                using (fFrameIDs)
                    yield return fFrameIDs;

                var frameIDs = new HashSet<UInt32>(
                    from key in fFrameIDs.Result select BitConverter.ToUInt32(key.Data.Array, key.Data.Offset)
                );

                var matchingTracebacks = new HashSet<UInt32>();

                for (int i = 0, c = instance.Snapshots.Count; i < c; i++) {
                    activity.Maximum = c;
                    activity.Progress = i;

                    var info = instance.Snapshots[i];

                    var fSnapshot = instance.GetSnapshot(info);
                    using (fSnapshot)
                        yield return fSnapshot;

                    var snapshot = fSnapshot.Result;
                    Func<HeapSnapshot.Traceback, bool> tracebackMatches = (traceback) => {
                        if (matchingTracebacks.Contains(traceback.ID))
                            return false;

                        var _f = traceback.Frames.Array;
                        for (int _i = 0, _c = traceback.Frames.Count, _o = traceback.Frames.Offset; _i < _c; _i++) {
                            if (frameIDs.Contains(_f[_i + _o]))
                                return true;
                        }

                        return false;
                    };

                    var fNewMatchingTracebacks = Future.RunInThread(() => new HashSet<UInt32>(
                        from traceback in snapshot.Tracebacks.AsParallel() where tracebackMatches(traceback) select traceback.ID
                    ));
                    yield return fNewMatchingTracebacks;

                    matchingTracebacks.UnionWith(fNewMatchingTracebacks.Result);

                    var fInfo = Future.RunInThread(() => new FilteredHeapSnapshotInfo(
                        (from heap in snapshot.Heaps.AsParallel() select (from alloc in heap.Allocations.AsParallel() where matchingTracebacks.Contains(alloc.TracebackID) select (long)alloc.Size).Sum()).Sum(),
                        (from heap in snapshot.Heaps.AsParallel() select (from alloc in heap.Allocations.AsParallel() where matchingTracebacks.Contains(alloc.TracebackID) select (long)alloc.Overhead).Sum()).Sum(),
                        (from heap in snapshot.Heaps.AsParallel() select (from alloc in heap.Allocations.AsParallel() where matchingTracebacks.Contains(alloc.TracebackID) select (long)(alloc.Size + alloc.Overhead)).Sum()).Sum(),
                        (from heap in snapshot.Heaps.AsParallel() select (from alloc in heap.Allocations.AsParallel() where matchingTracebacks.Contains(alloc.TracebackID) select alloc).Count()).Sum()
                    ));

                    yield return fInfo;
                    result[info] = fInfo.Result;

                    info.ReleaseStrongReference();
                }
            }

            CurrentFilterData = result;
            CurrentFilter = filter;
            PendingFilter = null;
            PendingFilterFuture = null;

            SnapshotTimeline.Invalidate();
        }

        public static string EscapeFilter (string filter) {
            filter = filter
                .Replace("*", "\x0")
                .Replace("?", "\x1");

            return String.Format(
                "^{0}$", 
                Regex.Escape(filter)
                    .Replace("\x0", "(.*)")
                    .Replace("\x1", "(.?)")
            );
        }

        public static Regex FilterToRegex (string rawFilter, bool compiled = false) {
            if (rawFilter == null)
                return null;

            var escaped = EscapeFilter(rawFilter);
            var options = RegexOptions.IgnoreCase;
            if (compiled)
                options |= RegexOptions.Compiled;

            return new Regex(escaped, options);
        }

        private void HeapFilter_FilterChanging (object sender, FilterChangingEventArgs args) {
            if (args.Filter.Trim().Length == 0)
                return;

            Regex regex;
            try {
                regex = FilterToRegex(args.Filter);
            } catch {
                args.SetValid(false);
                return;
            }

            foreach (var name in KnownFunctionNames) {
                if (regex.IsMatch(name)) {
                    args.SetValid(true);
                    return;
                }
            }

            args.SetValid(false);
        }

        private void HeapFilter_FilterChanged (object sender, EventArgs e) {
            if (HeapFilter.Filter != CurrentFilter) {
                if (PendingFilterFuture != null) {
                    if (HeapFilter.Filter == PendingFilter)
                        return;
                    else
                        PendingFilterFuture.Dispose();
                }

                PendingFilter = HeapFilter.Filter;
                PendingFilterFuture = Scheduler.Start(
                    FilterHeapData(Instance, HeapFilter.Filter), 
                    TaskExecutionPolicy.RunAsBackgroundTask
                );
            } else {
                PendingFilter = null;

                if (PendingFilterFuture != null) {
                    PendingFilterFuture.Dispose();
                    PendingFilterFuture = null;
                }
            }
        }

        protected IEnumerator<object> UpdateStats () {
            yield return Instance.UpdateFilteredTracebacks();

            var fDataSeries = Instance.GenerateTopFunctions();
            yield return fDataSeries;

            SnapshotTimeline.DataSeries.Clear();
            foreach (var kvp in fDataSeries.Result)
                SnapshotTimeline.DataSeries.Add(kvp.Key, kvp.Value);

            SnapshotTimeline.Invalidate();
        }

        private void StackFiltersMenu_Click (object sender, EventArgs e) {
            using (var dialog = new StackFiltersDialog()) {
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    if (Instance != null)
                        Start(UpdateStats());

                }
            }
        }

        private void ErrorDialogMenu_Click (object sender, EventArgs e) {
            if (!Program.ErrorList.Visible)
                Program.ErrorList.Show(this);
        }
    }
}
