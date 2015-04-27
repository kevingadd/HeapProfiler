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
using System.Linq;
using System.Windows.Forms;
using Squared.Data.Mangler;
using Squared.Task;
using System.Diagnostics;
using System.IO;
using Squared.Task.IO;
using System.Text;

namespace HeapProfiler {
    static class Program {
        public static TaskScheduler Scheduler;
        public static Tangle<object> Preferences;
        public static ErrorListDialog ErrorList;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main () {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (Scheduler = new TaskScheduler(JobQueue.WindowsMessageBased))
            using (ErrorList = new ErrorListDialog()) {
                Scheduler.ErrorHandler = OnTaskError;

                Preferences = new Tangle<object>(
                    Scheduler, CreatePreferencesStorage()
                );

                using (var f = Scheduler.Start(MainTask(), TaskExecutionPolicy.RunAsBackgroundTask)) {
                    f.RegisterOnComplete((_) => {
                        if (_.Failed)
                            Application.Exit();
                    }); 

                    Application.Run();
                }
            }
        }

        static StreamSource CreatePreferencesStorage () {
            var path = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create
            ), @"HeapProfiler");
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            return new SubStreamSource(new FolderStreamSource(path), "Preferences_", true);
        }

        static bool OnTaskError (Exception error) {
            MessageBox.Show(error.ToString(), "Unhandled exception in background task");
            return true;
        }

        public static IEnumerator<object> MainTask () {
            while (!Settings.DebuggingToolsInstalled) {
                // TODO: Add support for using the x64 debugging tools

                var defaultPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Microsoft SDKs\Windows\v7.1"
                );
                if (!Directory.Exists(defaultPath))
                    defaultPath = defaultPath.Replace(" (x86)", "");

                bool isSdkInstalled = Directory.Exists(defaultPath);

                defaultPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Microsoft SDKs\Windows\v7.1\Redist\Debugging Tools for Windows\dbg_x86.msi"
                );
                if (!File.Exists(defaultPath))
                    defaultPath = defaultPath.Replace(" (x86)", "");

                bool areRedistsInstalled = File.Exists(defaultPath);

                if (areRedistsInstalled) {
                    var result = MessageBox.Show("The x86 Debugging Tools for Windows from SDK 7.1 are not installed. Would you like to install them now?", "Error", MessageBoxButtons.YesNo);
                    if (result == DialogResult.No) {
                        Application.Exit();
                        yield break;
                    }

                    yield return RunProcess(new ProcessStartInfo(
                        "msiexec.exe", String.Format("/package \"{0}\"", defaultPath)
                    ));
                } else if (isSdkInstalled) {
                    MessageBox.Show("The x86 Debugging Tools for Windows from SDK 7.1 are not installed, and you did not install the redistributables when you installed the SDK. Please either install the debugging tools or the redistributables.", "Error");
                    Application.Exit();
                    yield break;
                } else {
                    var result = MessageBox.Show("Windows SDK 7.1 is not installed. Would you like to download the SDK?", "Error", MessageBoxButtons.YesNo);
                    if (result == DialogResult.Yes)
                        Process.Start("https://www.microsoft.com/en-us/download/details.aspx?id=8279");

                    Application.Exit();
                    yield break;
                }

                yield return new Sleep(1.0);
            }

            var args = Environment.GetCommandLineArgs().Skip(1);

            if (args.Count() > 0) {
                yield return OpenFilenames(args);

                Application.Exit();
            } else {
                using (var window = new MainWindow(Scheduler))
                    yield return window.Show();

                Application.Exit();
            }
        }

        public static IEnumerator<object> OpenFilenames (IEnumerable<string> filenames, MainWindow mainWindow = null) {
            var diffs = new HashSet<string>();
            var snapshots = new HashSet<string>();
            string recording = null;

            foreach (var filename in filenames) {
                switch (Path.GetExtension(filename)) {
                    case ".heaprecording": {
                        if (recording != null) {
                            MessageBox.Show("Only one heap recording can be opened at a time.", "Error");
                            continue;
                        } else if (snapshots.Count > 0) {
                            MessageBox.Show("You cannot open snapshots and a recording in the same session.", "Error");
                            continue;
                        }

                        recording = filename;
                    } break;
                    case ".heapsnap": {
                        if (recording != null) {
                            MessageBox.Show("You cannot open snapshots and a recording in the same session.", "Error");
                            continue;
                        }

                        snapshots.Add(filename);
                    } break;
                    case ".heapdiff": {
                        diffs.Add(filename);
                    } break;
                }
            }

            var futures = new OwnedFutureSet();
            var disposables = new HashSet<IDisposable>();

            if ((recording != null) || (snapshots.Count > 0)) {
                if (mainWindow == null) {
                    mainWindow = new MainWindow(Scheduler);
                    disposables.Add(mainWindow);
                    futures.Add(mainWindow.Show());
                }

                if (recording != null)
                    mainWindow.OpenRecording(recording);
                else
                    mainWindow.OpenSnapshots(snapshots);
            }

            foreach (var diff in diffs) {
                var viewer = new DiffViewer(Scheduler);
                disposables.Add(viewer);

                if (mainWindow != null)
                    futures.Add(viewer.Show(mainWindow));
                else
                    futures.Add(viewer.Show());

                yield return viewer.LoadDiff(diff);
            }

            if (futures.Count == 0) {
                if ((mainWindow == null) && (disposables.Count > 0))
                    throw new InvalidDataException();
            } else {
                using (futures)
                try {
                    yield return Future.WaitForAll(futures);
                } finally {
                    foreach (var disposable in disposables)
                        disposable.Dispose();
                }
            }
        }

        public static SignalFuture WaitForProcessExit (Process process) {
            var exited = new SignalFuture();

            process.Exited += (s, e) =>
                exited.Complete();

            if (process.HasExited)
                try {
                    exited.Complete();
                } catch {
                }

            return exited;
        }

        public static Future<Process> StartProcess (ProcessStartInfo psi) {
            return Future.RunInThread(
                () => {
                    var p = Process.Start(psi);
                    p.EnableRaisingEvents = true;
                    return p;
                }
            );
        }

        public static IEnumerator<object> RunProcess (
            ProcessStartInfo psi, ProcessPriorityClass? priority = null,
            IEnumerable<KeyValuePair<string, string>> customEnvironment = null
        ) {
            var rtc = new RunToCompletion<RunProcessResult>(RunProcessWithResult(
                psi, priority, customEnvironment
            ));
            yield return rtc;

            if ((rtc.Result.StdOut ?? "").Trim().Length > 0)
                ErrorList.ReportError(rtc.Result.StdOut);
            if ((rtc.Result.StdErr ?? "").Trim().Length > 0)
                ErrorList.ReportError(rtc.Result.StdErr);

            if (rtc.Result.ExitCode != 0)
                throw new Exception(String.Format("Process exited with code {0}", rtc.Result.ExitCode));
        }

        public static IEnumerator<object> RunProcessWithResult (
            ProcessStartInfo psi, ProcessPriorityClass? priority = null,
            IEnumerable<KeyValuePair<string, string>> customEnvironment = null
        ) {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            if (customEnvironment != null)
                foreach (var kvp in customEnvironment)
                    psi.EnvironmentVariables[kvp.Key] = kvp.Value;

            var fProcess = StartProcess(psi);
            yield return fProcess;

            if (priority.HasValue)
            try {
                fProcess.Result.PriorityClass = priority.Value;
            } catch {
            }

            using (var process = fProcess.Result)
            using (var stdout = new AsyncTextReader(
                new StreamDataAdapter(process.StandardOutput.BaseStream, false), Encoding.ASCII, 1024 * 16
            ))
            using (var stderr = new AsyncTextReader(
                new StreamDataAdapter(process.StandardError.BaseStream, false), Encoding.ASCII, 1024 * 16
            ))
            try {

                var fStdOut = stdout.ReadToEnd();
                var fStdErr = stderr.ReadToEnd();

                yield return WaitForProcessExit(process);

                yield return fStdOut;
                yield return fStdErr;

                yield return new Result(new RunProcessResult {
                    StdOut = fStdOut.Result,
                    StdErr = fStdErr.Result,
                    ExitCode = process.ExitCode
                });
            } finally {
                try {
                    if (!process.HasExited)
                        process.Kill();
                } catch {
                }
            }
        }
    }

    public class RunProcessResult {
        public string StdOut, StdErr;
        public int ExitCode;
    }
}
