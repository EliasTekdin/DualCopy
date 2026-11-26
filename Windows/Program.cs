using System;
using System.Windows.Forms;

namespace DualCopy.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => DualCopyLog.Error("Unhandled Windows Forms exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DualCopyLog.Error("Unhandled process exception.", args.ExceptionObject as Exception);
        DualCopyLog.Info("DualCopy Windows companion starting.");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        DualCopyLog.Info("DualCopy Windows companion stopped.");
    }
}
