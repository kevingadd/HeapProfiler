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

Original Author: Kevin Gadd (kevin.gadd@gmail.com)
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Squared.Task;
using System.IO;
using System.Windows.Forms;
using Squared.Util;
using Squared.Util.RegexExtensions;
using System.Text.RegularExpressions;

namespace HeapProfiler {
    public class RunningProcess : IDisposable {
        public const int SymbolResolveBatchSize = 1024;
        public const int MaxFramesPerTraceback = 31;

        public struct PendingSymbolResolve {
            public readonly UInt32 Frame;
            public readonly Future<TracebackFrame> Result;

            public PendingSymbolResolve (UInt32 frame, Future<TracebackFrame> result) {
                Frame = frame;
                Result = result;
            }
        }

        public readonly TaskScheduler Scheduler;
        public readonly ActivityIndicator Activities;
        public readonly OwnedFutureSet Futures = new OwnedFutureSet();
        public readonly List<HeapSnapshot> Snapshots = new List<HeapSnapshot>();
        public readonly HashSet<string> TemporaryFiles = new HashSet<string>();
        public readonly ProcessStartInfo StartInfo;
        public readonly IFuture LoadComplete;

        public event EventHandler StatusChanged;
        public event EventHandler SnapshotsChanged;

        public Process Process;

        protected readonly HashSet<HeapSnapshot.Module> SymbolModules = new HashSet<HeapSnapshot.Module>();
        protected readonly Dictionary<UInt32, TracebackFrame> ResolvedSymbolCache = new Dictionary<UInt32, TracebackFrame>();
        protected readonly Dictionary<UInt32, Future<TracebackFrame>> PendingSymbolResolves = new Dictionary<UInt32, Future<TracebackFrame>>();
        protected readonly BlockingQueue<PendingSymbolResolve> SymbolResolveQueue = new BlockingQueue<PendingSymbolResolve>();
        protected readonly LRUCache<Pair<string>, string> DiffCache = new LRUCache<Pair<string>, string>(32);

        protected RunningProcess (
            TaskScheduler scheduler,
            ActivityIndicator activities,
            ProcessStartInfo startInfo
        ) {
            StartInfo = startInfo;
            Activities = activities;

            Scheduler = scheduler;

            Futures.Add(Scheduler.Start(
                MainTask(), TaskExecutionPolicy.RunAsBackgroundTask
            ));
            StartHelperTasks();

            LoadComplete = new SignalFuture(true);

            DiffCache.ItemEvicted += DiffCache_ItemEvicted;
        }

        protected RunningProcess (
            TaskScheduler scheduler,
            ActivityIndicator activities,
            string[] snapshots
        ) {
            Scheduler = scheduler;
            Activities = activities;

            LoadComplete = Scheduler.Start(
                LoadSnapshots(snapshots),
                TaskExecutionPolicy.RunAsBackgroundTask
            );
            Futures.Add(LoadComplete);

            StartHelperTasks();

            DiffCache.ItemEvicted += DiffCache_ItemEvicted;
        }

        public IEnumerator<object> LoadSnapshots (IEnumerable<string> filenames) {
            var newSnaps = new List<HeapSnapshot>();

            int c = filenames.Count(), i = 0;
            using (var progress = Activities.AddItem("Loading snapshots"))
            foreach (var filename in filenames) {
                progress.Maximum = c;
                progress.Progress = i;

                var fSnapshot = Future.RunInThread(
                    () => new HeapSnapshot(filename)
                );

                yield return fSnapshot;
                newSnaps.Add(fSnapshot.Result);

                i += 1;
            }

            // Resort the loaded snapshots, since it's possible for the user to load
            //  a subset of a full capture, or load snapshots in the wrong order
            newSnaps.Sort((lhs, rhs) => lhs.When.CompareTo(rhs.When));

            Snapshots.Clear();
            Snapshots.AddRange(newSnaps);

            SymbolModules.Clear();
            using (var progress = Activities.AddItem("Scanning symbols"))
            foreach (var snap in Snapshots) {
                foreach (var module in snap.Modules)
                    SymbolModules.Add(module);

                yield return ResolveSymbolsForSnapshot(snap);
            }

            OnSnapshotsChanged();

            GC.Collect();
        }

        protected void StartHelperTasks () {
            Futures.Add(Scheduler.Start(
                SymbolResolverTask(), TaskExecutionPolicy.RunAsBackgroundTask
            ));
        }

        protected bool ResolveFrame (UInt32 frame, out TracebackFrame resolved, out Future<TracebackFrame> pendingResolve) {
            if (ResolvedSymbolCache.TryGetValue(frame, out resolved)) {
                pendingResolve = null;
                return true;
            }

            if (!PendingSymbolResolves.TryGetValue(frame, out pendingResolve)) {
                var f = PendingSymbolResolves[frame] = new Future<TracebackFrame>();
                var item = new PendingSymbolResolve(frame, f);

                SymbolResolveQueue.Enqueue(item);
            }

            return false;
        }

        protected IEnumerator<object> ResolveSymbolsForSnapshot (HeapSnapshot snapshot) {
            TracebackFrame tf;
            Future<TracebackFrame> ftf;
            var yield = new Yield();

            foreach (var traceback in snapshot.Tracebacks) {
                foreach (var frame in traceback.Frames)
                    ResolveFrame(frame, out tf, out ftf);

                yield return yield;
            }
        }

        protected IEnumerator<object> SymbolResolverTask () {
            var yield = new Yield();
            var batch = new List<PendingSymbolResolve>();
            var nullProgress = new CallbackProgressListener();
            int p = 0, c = 0;
            ActivityIndicator.Item progress = null;

            while (true) {
                var count = SymbolResolveBatchSize - batch.Count;
                SymbolResolveQueue.DequeueMultiple(batch, count);

                if (batch.Count == 0) {
                    p = c = 0;
                    if (progress != null) {
                        progress.Dispose();
                        progress = null;
                    }

                    var f = SymbolResolveQueue.Dequeue();
                    using (f)
                        yield return f;

                    batch.Add(f.Result);
                } else {
                    if (progress == null) {
                        progress = Activities.AddItem("Resolving symbols");
                        c = batch.Count + SymbolResolveQueue.Count;
                    } else {
                        c = p + SymbolResolveQueue.Count;
                        progress.Maximum = c;
                        progress.Progress = p;
                    }

                    string infile = Path.GetTempFileName(), outfile = Path.GetTempFileName();

                    var psi = new ProcessStartInfo(
                        Settings.UmdhPath, String.Format(
                            "-d \"{0}\" -f:\"{1}\"", infile, outfile
                        )
                    );

                    using (var sw = new StreamWriter(infile, false, Encoding.ASCII)) {
                        sw.WriteLine("// Loaded modules:");
                        sw.WriteLine("//     Base Size Module");

                        foreach (var module in SymbolModules)
                            sw.WriteLine(
                                "//            {0:X8} {1:X8} {2}", 
                                module.Offset, module.Size, module.Filename
                            );

                        sw.WriteLine("//");
                        sw.WriteLine("// Process modules enumerated.");

                        sw.WriteLine();
                        sw.WriteLine("*- - - - - - - - - - Heap 0 Hogs - - - - - - - - - -");
                        sw.WriteLine();

                        for (int i = 0, j = 0; i < batch.Count; i++) {
                            if ((i == 0) || (i % MaxFramesPerTraceback == 0)) {
                                sw.WriteLine(
                                    "{0:X8} bytes + {1:X8} at {2:X8} by BackTrace{3:X8}",
                                    1, 0, j, j
                                );
                                j += 1;
                            }

                            sw.WriteLine("\t{0:X8}", batch[i].Frame);
                        }
                    }

                    using (Finally.Do(() => {
                        try {
                            File.Delete(infile);
                        } catch {
                        }
                    }))
                    using (var rp = Scheduler.Start(Program.RunProcess(psi), TaskExecutionPolicy.RunAsBackgroundTask))
                        yield return rp;

                    using (Finally.Do(() => {
                        try {
                            File.Delete(outfile);
                        } catch {
                        }
                    })) {
                        var rtc = new RunToCompletion<HeapDiff>(
                            HeapDiff.FromFile(outfile, nullProgress)
                        );

                        using (rtc)
                            yield return rtc;

                        int i = 0;
                        foreach (var traceback in rtc.Result.Tracebacks) {
                            foreach (var frame in traceback.Value.Frames) {
                                var key = batch[i].Frame;
                                batch[i].Result.Complete(frame);
                                ResolvedSymbolCache[key] = frame;
                                PendingSymbolResolves.Remove(key);
                                i += 1;
                            }

                            yield return yield;
                        }

                        foreach (var frame in batch) {
                            if (frame.Result.Completed)
                                continue;

                            Console.WriteLine("Frame {0:x8} could not be resolved!", frame.Frame);

                            var tf = new TracebackFrame(frame.Frame);
                            frame.Result.Complete(tf);
                            ResolvedSymbolCache[frame.Frame] = tf;
                            PendingSymbolResolves.Remove(frame.Frame);
                        }
                    }

                    p += batch.Count;
                    batch.Clear();
                }
            }
        }

        void DiffCache_ItemEvicted (KeyValuePair<Pair<string>, string> item) {
            if (TemporaryFiles.Contains(item.Value)) {
                TemporaryFiles.Remove(item.Value);

                try {
                    File.Delete(item.Value);
                } catch {
                }
            }
        }

        protected void OnStatusChanged () {
            if (StatusChanged != null)
                StatusChanged(this, EventArgs.Empty);
        }

        protected void OnSnapshotsChanged () {
            if (SnapshotsChanged != null)
                SnapshotsChanged(this, EventArgs.Empty);
        }

        protected IEnumerator<object> MainTask () {
            var shortName = Path.GetFileName(Path.GetFullPath(StartInfo.FileName));

            using (Activities.AddItem("Enabling heap instrumentation"))
            yield return Program.RunProcess(new ProcessStartInfo(
                Settings.GflagsPath, String.Format(
                    "-i \"{0}\" +ust", shortName
                )
            ));

            var f = Program.StartProcess(StartInfo);
            using (Activities.AddItem("Starting process"))
                yield return f;

            using (Process = f.Result) {
                OnStatusChanged();
                yield return Program.WaitForProcessExit(Process);
            }

            Process = null;

            using (Activities.AddItem("Disabling heap instrumentation"))
            yield return Program.RunProcess(new ProcessStartInfo(
                Settings.GflagsPath, String.Format(
                    "-i \"{0}\" -ust", shortName
                )
            ));

            OnStatusChanged();
        }

        public static RunningProcess Start (
            TaskScheduler scheduler, ActivityIndicator activities, string executablePath, string arguments, string workingDirectory
        ) {
            var psi = new ProcessStartInfo(
                executablePath, arguments
            );
            psi.UseShellExecute = false;

            if ((workingDirectory != null) && (workingDirectory.Trim().Length > 0))
                psi.WorkingDirectory = workingDirectory;
            else
                psi.WorkingDirectory = Path.GetDirectoryName(executablePath);

            return new RunningProcess(scheduler, activities, psi);
        }

        public bool Running {
            get {
                if (Process == null)
                    return false;

                return !Process.HasExited;
            }
        }

        public void Dispose () {
            Snapshots.Clear();

            foreach (var fn in TemporaryFiles) {
                try {
                    File.Delete(fn);
                } catch {
                }
            }
            TemporaryFiles.Clear();

            if (Process != null) {
                Process.Dispose();
                Process = null;
            }

            Futures.Dispose();
        }

        public IFuture CaptureSnapshot () {
            var filename = Path.GetTempFileName();

            var f = Scheduler.Start(
                CaptureSnapshotTask(filename), TaskExecutionPolicy.RunAsBackgroundTask
            );

            Futures.Add(f);
            return f;
        }

        protected IEnumerator<object> CaptureSnapshotTask (string targetFilename) {
            var now = DateTime.Now;

            var mem = new MemoryStatistics(Process);

            var psi = new ProcessStartInfo(
                Settings.UmdhPath, String.Format(
                    "-p:{0} -f:\"{1}\"", Process.Id, targetFilename
                )
            );

            TemporaryFiles.Add(targetFilename);

            using (Activities.AddItem("Capturing heap snapshot"))
                yield return Program.RunProcess(psi);

            using (Activities.AddItem("Loading snapshot")) {
                yield return Future.RunInThread(
                    () => File.AppendAllText(targetFilename, mem.GetFileText())
                );

                var fSnapshot = Future.RunInThread(
                    () => new HeapSnapshot(
                        Snapshots.Count + 1, now, targetFilename
                    )
                );
                yield return fSnapshot;

                var snap = fSnapshot.Result;

                Snapshots.Add(snap);

                using (var progress = Activities.AddItem("Scanning symbols")) {
                    foreach (var module in snap.Modules)
                        SymbolModules.Add(module);

                    yield return ResolveSymbolsForSnapshot(snap);
                }
            }

            OnSnapshotsChanged();
        }

        public IEnumerator<object> DiffSnapshots (string file1, string file2) {
            file1 = Path.GetFullPath(file1);
            file2 = Path.GetFullPath(file2);
            var pair = Pair.New(file1, file2);

            string filename;
            if (DiffCache.TryGetValue(pair, out filename)) {
                yield return new Result(filename);
            } else {
                filename = Path.GetTempFileName();

                var psi = new ProcessStartInfo(
                    Settings.UmdhPath, String.Format(
                        "-d \"{0}\" \"{1}\" -f:\"{2}\"", file1, file2, filename
                    )
                );

                var rp = Scheduler.Start(Program.RunProcess(psi), TaskExecutionPolicy.RunAsBackgroundTask);

                using (Activities.AddItem("Generating heap diff"))
                using (rp)
                    yield return rp;

                DiffCache[pair] = filename;
                TemporaryFiles.Add(filename);

                yield return new Result(filename);
            }
        }

        public static RunningProcess FromSnapshots (TaskScheduler scheduler, ActivityIndicator activities, string[] snapshots) {
            return new RunningProcess(
                scheduler, activities, snapshots
            );
        }
    }
}
