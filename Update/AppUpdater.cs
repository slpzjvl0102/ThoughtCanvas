using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace ThoughtCanvas;

public enum UpdateCheckResult { NotInstalled, NoToken, UpToDate, Ready, Failed }

// GitHub Releases에서 새 버전을 확인·다운로드합니다 (Velopack).
// 저장소가 private이라 각 기기 설정에 저장한 읽기 전용 GitHub token으로 Release에 접근합니다.
// 받아 둔 업데이트는 "재시작하여 업데이트"로 바로 적용하거나, 다음 실행 때 자동 적용됩니다.
// 사용자 데이터(%LOCALAPPDATA%\ThoughtCanvas)는 설치 폴더(%LOCALAPPDATA%\ThoughtCanvasApp) 밖이라 유지됩니다.
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/slpzjvl0102/ThoughtCanvas";
    // Velopack packId — 데이터 폴더(ThoughtCanvas)와 겹치지 않아야 제거 시 데이터가 지워지지 않습니다.
    public const string PackId = "ThoughtCanvasApp";

    private readonly UpdateManager local = new(new SimpleWebSource("https://invalid.local/"));
    private VelopackAsset? ready;
    private UpdateManager? readyManager;
    private int busy;

    // Setup.exe로 설치된 경우에만 업데이트할 수 있습니다 (개발 실행·압축 해제 실행은 대상 아님)
    public bool IsInstalled => local.IsInstalled;

    public string CurrentVersion => local.CurrentVersion?.ToString() ?? AssemblyVersion;

    public static string AssemblyVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public string? ReadyVersion => (ready ?? (IsInstalled ? local.UpdatePendingRestart : null))?.Version.ToString();

    public string? LastError { get; private set; }

    public async Task<UpdateCheckResult> CheckAndDownloadAsync(string token, CancellationToken ct)
    {
        if (!IsInstalled) return UpdateCheckResult.NotInstalled;
        if (ReadyVersion is not null) return UpdateCheckResult.Ready;
        if (string.IsNullOrWhiteSpace(token)) return UpdateCheckResult.NoToken;
        if (Interlocked.Exchange(ref busy, 1) == 1) return UpdateCheckResult.UpToDate;
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, token, prerelease: false));
            var info = await manager.CheckForUpdatesAsync();
            if (info is null) { LastError = null; return UpdateCheckResult.UpToDate; }
            await manager.DownloadUpdatesAsync(info, null, ct);
            ready = info.TargetFullRelease; readyManager = manager; LastError = null;
            return UpdateCheckResult.Ready;
        }
        catch (OperationCanceledException) { return UpdateCheckResult.UpToDate; }
        catch (Exception ex)
        {
            // 오프라인·token 만료·GitHub 일시 오류 — 다음 주기에 다시 시도합니다
            LastError = ex.Message.Contains("401") || ex.Message.Contains("404")
                ? "GitHub token을 확인하세요 (만료되었거나 ThoughtCanvas 저장소 Contents 읽기 권한이 없습니다)."
                : ex.Message;
            return UpdateCheckResult.Failed;
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    // 앱이 정상 종료(저장)된 뒤 업데이트를 적용하고 다시 실행하도록 예약합니다. 호출 후 창을 평소처럼 닫으면 됩니다.
    public bool ScheduleApplyOnExit()
    {
        var asset = ready ?? local.UpdatePendingRestart;
        if (asset is null) return false;
        (readyManager ?? local).WaitExitThenApplyUpdates(asset, silent: true, restart: true);
        return true;
    }
}
