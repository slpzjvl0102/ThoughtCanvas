using System.Windows;

namespace ThoughtCanvas;

// 첫 실행 설정: 이 기기에 settings.json이 없을 때(새로 설치한 사용자) 한 번만 설정 창을 띄웁니다.
// 기존 사용자는 설정 파일이 이미 있어 나타나지 않습니다. 닫아도 다시 묻지 않고, 작업 → 연결 설정에서 바꿀 수 있습니다.
public partial class MainWindow
{
    private void RunFirstSetup()
    {
        // Google Drive 데스크톱 앱 폴더(내 드라이브/My Drive)를 찾아 채우고, 없으면 비워 둡니다
        if (!Directory.Exists(settings.DriveSyncFolder)) settings.DriveSyncFolder = FindGoogleDriveFolder() ?? "";
        Activate();
        bool saved = Dialogs.Settings(this, settings, firstRun: true);
        store.SaveSettings(settings);
        SetStatus(saved ? "처음 설정을 저장했습니다." : "기본 설정으로 시작합니다 · 작업 → 연결 설정에서 바꿀 수 있습니다.");
    }

    private static string? FindGoogleDriveFolder()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                foreach (var name in new[] { "내 드라이브", "My Drive" })
                {
                    string path = Path.Combine(drive.RootDirectory.FullName, name);
                    if (Directory.Exists(path)) return path;
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
