using System.Configuration;
using System.Data;
using System.Windows;

namespace ThoughtCanvas;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string report = e.Args.SkipWhile(a => a != "--self-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "self-test.txt");
            int code = SelfTests.Run(report); Shutdown(code); return;
        }
        string dataRoot = Environment.GetEnvironmentVariable("THOUGHTCANVAS_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThoughtCanvas");
        string suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..20];
        instance = new Mutex(true, "Local\\ThoughtCanvas_" + suffix, out bool created);
        if (!created) { MessageBox.Show("같은 데이터 위치를 사용하는 ThoughtCanvas가 이미 실행 중입니다.", "ThoughtCanvas"); Shutdown(); return; }
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "ThoughtCanvas 시작 실패"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}

