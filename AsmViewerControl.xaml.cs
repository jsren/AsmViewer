//------------------------------------------------------------------------------
// <copyright file="AsmViewerControl.xaml.cs" company="Company">
//     Copyright (c) Company. All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

namespace RealtimeAsm
{
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Threading;
    using System.Diagnostics;
    using System.Linq;
    using Microsoft.VisualStudio.VCCodeModel;
    using System.Windows.Input;

    using System;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Threading;
    using Microsoft.VisualStudio.VCProjectEngine;

    public class CompilerEntry
    {
        public string Name { get; set; }
        public string Filepath { get; set; }
    }

    public enum MsgType
    {
        Info,
        Warning,
        Error,
        Unknown
    }
    
    public class CompilerMessage
    {
        public string Message;
        public MsgType Type;
    }

    public static class DTEHelperEx
    {
        public static EnvDTE80.DTE2 GetDTE(this IServiceProvider serviceProvider) {
            return (EnvDTE80.DTE2)serviceProvider.GetService(typeof(EnvDTE.DTE));
        }
    }

    public struct AsmReference
    {
        public string Name { get; }
        public int Offset { get; }

        public AsmReference(string name, int offset)
        {
            Name   = name;
            Offset = offset;
        }
    }

    public class AsmLine
    {
        public int Offset { get; set; }
        public string Opcode { get; set; }
        public string Operands { get; set; }
        public string MachineCode { get; set; }
        public string Comment { get; set; }
        public string SourceFile { get; set; }
        public int SourceFileLine { get; set; }
        public AsmReference? Reference { get; set; }
    }

    public class AsmFunction
    {
        public string Name { get; }
        public int LineNumber { get; }
        public IEnumerable<AsmLine> Assembly { get; }

        public AsmFunction(string name, int lineNumber)
        {
            Name       = name;
            LineNumber = lineNumber;
            Assembly   = new List<AsmLine>();
        }
    }




    /// <summary>
    /// Interaction logic for AsmViewerControl.
    /// </summary>
    public partial class AsmViewerControl : UserControl
    {
        private IServiceProvider serviceProvider;
        private EnvDTE.Events dteEvents;
        private EnvDTE.DocumentEvents dteDocEvents;
        private EnvDTE.TextEditorEvents dteTextEditorEvents;
        private EnvDTE.SelectionEvents dteSelectionEvents;

        private volatile bool disassemblyQueued = false;
        private AutoResetEvent disassemblyCV = new AutoResetEvent(true);

        private Guid instanceID;

        private string objfile;
        private CompilerEntry compiler;

        private List<CompilerMessage> messages = new List<CompilerMessage>();

        private string disasmFilepath = @"P:\cygwin64\bin\objdump.exe";

        /// <summary>
        /// Initializes a new instance of the <see cref="AsmViewerControl"/> class.
        /// </summary>
        public AsmViewerControl(IServiceProvider serviceProvider)
        {
            this.instanceID = Guid.NewGuid();
            objfile = $"_obj_{instanceID}.o";

            compiler = new CompilerEntry()
            {
                Name = "GCC 5.4.0",
                Filepath = @"P:\cygwin64\bin\g++.exe"
            };

            this.serviceProvider = serviceProvider;
            this.InitializeComponent();

            this.Loaded += AsmViewerControl_Loaded;

        }

        private void AsmViewerControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Configure save event
            dteEvents = serviceProvider.GetDTE().Events;
            dteDocEvents = dteEvents.DocumentEvents;
            dteTextEditorEvents = dteEvents.TextEditorEvents;
            dteSelectionEvents = dteEvents.SelectionEvents;

            dteDocEvents.DocumentSaved += DocumentEvents_DocumentSaved;
        }


        ~AsmViewerControl()
        {
            // Delete the object file
            try {
                System.IO.File.Delete(objfile);
            }
            catch { }
        }


        private void DocumentEvents_DocumentSaved(EnvDTE.Document Document)
        {
            if (compiler == null || Document?.FullName == null) return;

            Task.Run((Action)this.DoDisassembly);
        }


        private string TargetFile
        {
            get
            {
                try {
                    return serviceProvider.GetDTE().ActiveDocument?.FullName;
                }
                catch (Exception e)
                {
                    Debug.Print(e.ToString());
                    return null;
                }
            }
        }

        private string IncludePaths
        {
            get
            {
                try
                {
                    var proj = serviceProvider.GetDTE().Solution.Projects.Item(1);
                    var cppProj = proj.Object as VCProject;

                    var cfgs = (IVCCollection)cppProj.Configurations;
                    var cfg = (VCConfiguration)cfgs.Item(1);
                    var tools = (IVCCollection)cfg.Tools;

                    VCCLCompilerTool cppCfg = null; VCNMakeTool nmakeCfg = null;
                    string solutionDir = System.IO.Path.GetDirectoryName(proj.DTE.Solution.FileName);

                    foreach (var tool in tools) {
                        if ((cppCfg = tool as VCCLCompilerTool) != null) break;
                        else if ((nmakeCfg = tool as VCNMakeTool) != null) break;
                    }
                    if (cppCfg != null)
                    {
                        var defines = cppCfg.PreprocessorDefinitions.Split(';').Select((s) => "-D" + s);
                        var includes = cppCfg.AdditionalIncludeDirectories.Split(';')
                            .Select((s) => "-I\"" + s.Replace("$(SolutionDir)", solutionDir) + '\"');

                        return defines.Union(includes).Where((s) => !s.Contains("$(")).Aggregate((s, x) => s + ' ' + x);
                    }
                    else if (nmakeCfg != null)
                    {
                        var defines = nmakeCfg.PreprocessorDefinitions.Split(';').Select((s) => "-D" + s);
                        var includes = nmakeCfg.IncludeSearchPath.Split(';')
                            .Select((s) => "-I\"" + s.Replace("$(SolutionDir)", solutionDir) + '\"');

                        return defines.Union(includes).Where((s) => !s.Contains("$(")).Aggregate((s, x) => s + ' ' + x);
                    }
                    else return "";
                }
                catch (Exception e)
                {
                    Debug.Print(e.ToString());
                    return "";
                }
            }
        }

        int currentSourceFileLine;
        string currentSourceFile;
        AsmFunction currentFunction = null;
        List<AsmFunction> functions = new List<AsmFunction>();

        int disasmLineNo = 0;
        private void Disasm_DataRecevied(object sender, DataReceivedEventArgs e)
        {
            Regex functionPattern = new Regex(@"^\d+ \<(.*)\>:\s*$");
            Regex linePattern = new Regex(@"^\s*([0-9A-Fa-f]+):\s*(.*)(?: )*\t\s*([^\<#\s]*)([^\<#]*)?\s*(#.*)?\s*(?:\<([^\+]*)(?:\+0x([0-9A-Fa-f]+))\>)?\s*$");
            Regex locationPattern = new Regex(@"^((?:[A-Z]:\\)?[^:]+):(\d+)\s*$");

            if (e.Data == null) return;

            Match funcMatch = functionPattern.Match(e.Data);
            if (funcMatch.Success)
            {
                if (currentFunction != null)
                {
                    lock (functions) { 
                        functions.Add(currentFunction);
                    }
                }
                currentFunction = new AsmFunction(funcMatch.Groups[1].Value, disasmLineNo);
            }
            else if (currentFunction != null)
            {
                Match lineMatch = linePattern.Match(e.Data);
                if (lineMatch.Success)
                {
                    (currentFunction.Assembly as List<AsmLine>)?.Add(new AsmLine()
                    {
                        Offset      = Convert.ToInt32(lineMatch.Groups[1].Value, 16),
                        MachineCode = lineMatch.Groups[2].Value.Trim(),
                        Opcode      = lineMatch.Groups[3].Value.Trim(),
                        Operands    = lineMatch.Groups[4].Value.Trim(),
                        Comment     = lineMatch.Groups[5].Value,
                        Reference   = new AsmReference(lineMatch.Groups[6].Value, lineMatch.Groups[7].Success ? 
                                        Convert.ToInt32(lineMatch.Groups[7].Value, 16) : 0),
                        SourceFile  = currentSourceFile,
                        SourceFileLine = currentSourceFileLine
                    });
                }
                else
                {
                    Match locMatch = locationPattern.Match(e.Data);
                    if (locMatch.Success)
                    {
                        currentSourceFile     = locMatch.Groups[1].Value;
                        currentSourceFileLine = Convert.ToInt32(locMatch.Groups[2].Value);
                    }
                }
            }

            disasmLineNo += 1;
        }

        private void Compiler_OutputMsgReceived(object sender, DataReceivedEventArgs e)
        {
            lock (messages) {
                messages.Add(new CompilerMessage() { Message = e.Data, Type = MsgType.Info });
            }
        }

        private void Compiler_ErrorMsgReceived(object sender, DataReceivedEventArgs e)
        {
            lock (messages) {
                messages.Add(new CompilerMessage() { Message = e.Data, Type = MsgType.Error });
            }
        }

        private void ShowError(string message)
        {
            MessageBox.Show($"Error: {message}", "AsmViewer");
        }

        private void DoDisassembly()
        {
            // Check if one already running (TODO: There used to be a justification for using CVs!)
            if (!disassemblyCV.WaitOne(1)) return;

            try
            {
                string includes = IncludePaths;
                string flags = "";
                Dispatcher.Invoke((Action)delegate
                {
                    loadingText.Content = "Initializing...";
                    progress.Visibility = Visibility.Visible;
                    loadingGrid.Visibility = Visibility.Visible;

                    messageStack.Children.Clear();
                    messageStack.Children.Add(asmGrid);
                    asmGrid.Clear();

                    flags = flagsText.Text;
                });

                lock (messages) {
                    messages.Clear();
                }
                lock (functions)
                {
                    functions.Clear();
                    currentFunction = null;
                }


                // Delete the object file
                try { System.IO.File.Delete(objfile); }
                catch { }

                ProcessStartInfo info = new ProcessStartInfo()
                {
                    FileName = compiler.Filepath,
                    Arguments = $"-g -o \"{objfile}\" {includes} {flags} -c \"{TargetFile}\"",

                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var proc = Process.Start(info);
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();

                proc.ErrorDataReceived += Compiler_ErrorMsgReceived;
                proc.OutputDataReceived += Compiler_OutputMsgReceived;

                Dispatcher.Invoke((Action)delegate {
                    loadingText.Content = "Compiling...";
                });

                if (!proc.WaitForExit(10000))
                {
                    ShowError("The operation timed out.");
                    return;
                }

                if (proc.ExitCode == 0)
                {
                    info = new ProcessStartInfo()
                    {
                        FileName = disasmFilepath,
                        Arguments = $"-C -l -d \"{objfile}\"",

                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    proc = Process.Start(info);
                    proc.BeginErrorReadLine();
                    proc.BeginOutputReadLine();

                    proc.OutputDataReceived += Disasm_DataRecevied;

                    Dispatcher.Invoke((Action)delegate {
                        loadingText.Content = "Disassembling...";
                    });

                    if (!proc.WaitForExit(10000))
                    {
                        ShowError("The operation timed out.");
                        return;
                    }
                }


                Dispatcher.BeginInvoke((Action)delegate
                {
                    lock (messages)
                    {
                        foreach (var msg in messages)
                        {
                            messageStack.Children.Add(new TextBlock() { Text = msg.Message });
                        }
                    }
                    lock (functions)
                    {
                        foreach (var function in functions)
                        {
                            asmGrid.AddFunction(function);
                        }
                    }
                    loadingGrid.Visibility = Visibility.Collapsed;
                });
            }
            finally
            {
                // Release condition variable
                disassemblyCV.Set();
            }
        }


        private void helpButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("http://www.imada.sdu.dk/Courses/DM18/Litteratur/IntelnATT.htm");
        }

        private void OnMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                //this.asmGrid.FontSize += e.Delta;
            }
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                this.asmGrid.FontSize += e.Delta / 120.0;
            }
        }
    }
}
