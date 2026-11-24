using System.Windows;
using System.Windows.Threading;

namespace ThoughtCanvas;

// 자동 업데이트 (GitHub Releases, Velopack): 시작 10초 뒤와 이후 6시간마다 확인해 백그라운드로 받아 둡니다.
// 작업 → "재시작하여 업데이트"로 바로 적용하거나, 앱을 닫을 때 자동 적용됩니다 (다음 실행은 새 버전).
public partial class MainWindow
{
    private readonly AppUpdater updater = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private void InitUpdater()
    {
        RefreshUpdateMenu();
        if (!updater.IsInstalled) return;
        updateTimer.Tick += async (_, _) => { updateTimer.Interval = TimeSpan.FromHours(6); await CheckForUpdate(false); };
        updateTimer.Start();
        // 받아 둔 업데이트는 창을 닫을 때(저장 완료 후) 적용해, 다음 실행이 바로 새 버전이 되게 합니다
        Closed += (_, _) => { updateTimer.Stop(); updater.ScheduleApplyOnExit(restart: false); };
    }

    private async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (updater.ReadyVersion is not null)
        {
            if (saving) { SetStatus("저장을 마친 뒤 다시 눌러 주세요."); return; }
            if (updater.ScheduleApplyOnExit()) Close();
            return;
        }
        await CheckForUpdate(true);
    }

    private async Task CheckForUpdate(bool manual)
    {
        if (manual) SetStatus("업데이트 확인 중…");
        var result = await updater.CheckAndDownloadAsync(lifetime.Token);
        RefreshUpdateMenu();
        switch (result)
        {
            case UpdateCheckResult.Ready:
                SetStatus($"새 버전 v{updater.ReadyVersion} 준비됨 · 작업 → 재시작하여 업데이트 (닫을 때 자동 적용)");
                break;
            case UpdateCheckResult.UpToDate when manual:
                SetStatus($"최신 버전입니다 · v{updater.CurrentVersion}");
                break;
            case UpdateCheckResult.NotInstalled when manual:
                Dialogs.Message(this, "업데이트", "설치 버전(ThoughtCanvasApp-win-Setup.exe)에서만 자동 업데이트를 쓸 수 있습니다.");
                break;
            case UpdateCheckResult.Failed:
                SetStatus("업데이트 확인 실패 · " + updater.LastError);
                if (manual) Dialogs.Message(this, "업데이트 확인 실패", updater.LastError ?? "알 수 없는 오류");
                break;
        }
    }

    private void RefreshUpdateMenu() =>
        UpdateMenu.Header = updater.ReadyVersion is { } v ? $"재시작하여 업데이트 (v{v})" : $"업데이트 확인 (현재 v{updater.CurrentVersion})";
}
