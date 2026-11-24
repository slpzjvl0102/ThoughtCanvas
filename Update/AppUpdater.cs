using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace ThoughtCanvas;

public enum UpdateCheckResult { NotInstalled, UpToDate, Ready, Failed }

// GitHub Releases에서 새 버전을 확인·다운로드합니다 (Velopack).
// 받아 둔 업데이트는 "재시작하여 업데이트"로 바로 적용하거나, 다음 실행 때 자동 적용됩니다.
// 사용자 데이터(%LOCALAPPDATA%\ThoughtCanvas)는 설치 폴더(%LOCALAPPDATA%\ThoughtCanvasApp) 밖이라 유지됩니다.
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/slpzjvl0102/ThoughtCanvas";
    // Velopack packId — 데이터 폴더(ThoughtCanvas)와 겹치지 않아야 제거 시 데이터가 지워지지 않습니다.
    public const string PackId = "ThoughtCanvasApp";

    private readonly UpdateManager manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    private VelopackAsset? ready;
    private int busy;

    // Setup.exe로 설치된 경우에만 업데이트할 수 있습니다 (개발 실행·압축 해제 실행은 대상 아님)
    public bool IsInstalled => manager.IsInstalled;

    public string CurrentVersion => manager.CurrentVersion?.ToString() ?? AssemblyVersion;

    public static string AssemblyVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public string? ReadyVersion => (ready ?? (IsInstalled ? manager.UpdatePendingRestart : null))?.Version.ToString();

    public string? LastError { get; private set; }

    public async Task<UpdateCheckResult> CheckAndDownloadAsync(CancellationToken ct)
    {
        if (!IsInstalled) return UpdateCheckResult.NotInstalled;
        if (ReadyVersion is not null) return UpdateCheckResult.Ready;
        if (Interlocked.Exchange(ref busy, 1) == 1) return UpdateCheckResult.UpToDate;
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            if (info is null) { LastError = null; return UpdateCheckResult.UpToDate; }
            await manager.DownloadUpdatesAsync(info, null, ct);
            ready = info.TargetFullRelease; LastError = null;
            return UpdateCheckResult.Ready;
        }
        catch (OperationCanceledException) { return UpdateCheckResult.UpToDate; }
        catch (Exception ex)
        {
            // 오프라인·GitHub 일시 오류 — 다음 주기에 다시 시도합니다
            LastError = ex.Message;
            return UpdateCheckResult.Failed;
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    private bool scheduled;

    // 앱이 정상 종료(저장)된 뒤 업데이트를 적용하도록 예약합니다. 호출 후 창을 평소처럼 닫으면 됩니다.
    // restart: true면 적용 후 다시 실행("재시작하여 업데이트"), false면 적용만 하고 끝냅니다(그냥 닫을 때).
    public bool ScheduleApplyOnExit(bool restart = true)
    {
        if (scheduled || !IsInstalled) return scheduled;
        var asset = ready ?? manager.UpdatePendingRestart;
        if (asset is null) return false;
        manager.WaitExitThenApplyUpdates(asset, silent: true, restart: restart);
        scheduled = true;
        return true;
    }
}
