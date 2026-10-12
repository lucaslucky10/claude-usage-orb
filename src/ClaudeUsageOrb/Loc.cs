using System;
using System.Globalization;

namespace ClaudeUsageOrb;

/// <summary>Korean / English UI strings.</summary>
internal static class Loc
{
    public static bool Ko { get; private set; }

    public static void Init(string? language)
    {
        string lang = string.IsNullOrWhiteSpace(language)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : language;
        Ko = string.Equals(lang, "ko", StringComparison.OrdinalIgnoreCase);
    }

    private static string T(string ko, string en) => Ko ? ko : en;

    public static string AppName => "Claude Usage Orb";
    public static string PopupTitle => T("Claude 사용량", "Claude usage");

    // Limits
    public static string SessionLabel => T("세션 (5시간)", "Session (5h)");
    public static string SessionShort => "5h";
    public static string WeeklyLabel => T("주간 · 전체 모델", "Weekly · all models");
    public static string WeeklyShort => T("주간", "week");
    public static string WeeklyScopedLabel(string name) => T($"주간 · {name}", $"Weekly · {name}");
    public static string Remaining(string pct) => T($"{pct}% 남음", $"{pct}% left");
    public static string ResetsIn(string duration) => T($"{duration} 후 초기화", $"resets in {duration}");
    public static string ResetPending => T("초기화됨 · 갱신 대기", "reset · refreshing");
    public static string PaceWarning(string duration) => T($"이 속도면 약 {duration} 후 소진", $"at this pace, runs out in ~{duration}");
    public static string BoundByWeekly(string pct) => T($"주간 전체 한도 때문에 실제로는 약 {pct}%만 사용 가능", $"weekly cap limits this to ~{pct}% in practice");
    public static string ExtraUsage(string used, string limit) => T($"추가 사용량 {used} / {limit}", $"Extra usage {used} / {limit}");
    public static string ExtraUsageNoLimit(string used) => T($"추가 사용량 {used}", $"Extra usage {used}");

    // Time
    public static string Today => T("오늘", "today");
    public static string Tomorrow => T("내일", "tomorrow");
    public static string LessThanMinute => T("1분 미만", "<1 min");
    public static string Days(int d) => T($"{d}일", $"{d}d");
    public static string Hours(int h) => T($"{h}시간", $"{h}h");
    public static string Minutes(int m) => T($"{m}분", $"{m}m");
    public static string JustNow => T("방금 전", "just now");
    public static string Ago(string duration) => T($"{duration} 전", $"{duration} ago");
    public static string UpdatedAgo(string ago) => T($"업데이트 {ago}", $"updated {ago}");

    // Status
    public static string Loading => T("불러오는 중…", "Loading…");
    public static string NoCredentials => T(
        "Claude Code 로그인 정보를 찾지 못했습니다. 터미널에서 claude 를 실행해 로그인하세요.",
        "Claude Code login not found. Run `claude` in a terminal and sign in.");
    public static string AuthExpired => T(
        "로그인 토큰이 만료됐습니다. 터미널에서 claude 를 한 번 실행하거나, 우클릭 메뉴에서 '로그인 만료 시 자동 갱신'을 켜세요.",
        "Login token expired. Run `claude` once in a terminal, or turn on 'Renew expired login automatically' in the menu.");
    public static string AuthRenewing => T("로그인 토큰이 만료돼 자동으로 갱신하는 중…", "Login token expired, renewing…");
    public static string AuthRenewFailed => T(
        "자동 갱신에 실패했습니다. 터미널에서 claude 를 실행해 다시 로그인하세요.",
        "Automatic renewal failed. Run `claude` in a terminal and sign in again.");
    public static string RateLimited(string retry) => T(
        $"서버 요청 한도에 걸려 {retry} 후 다시 받아옵니다. 같은 계정을 쓰는 다른 PC·앱이 많을수록 자주 걸립니다.",
        $"Rate limited by the server, retrying in {retry}. Other PCs/apps on the same account share this limit.");
    public static string RefreshScheduled(string when) => T($"새로고침 예약됨 · {when} 후", $"Refresh scheduled in {when}");
    public static string NetworkError(string retry) => T($"네트워크 오류 · {retry} 후 재시도", $"Network error, retrying in {retry}");
    public static string ServerError(int code, string retry) => T($"서버 오류 ({code}) · {retry} 후 재시도", $"Server error ({code}), retrying in {retry}");
    public static string ShowingCached => T("마지막으로 받은 값을 표시하는 중", "showing last known values");
    public static string Hint => T("더블클릭: 새로고침 · 우클릭: 메뉴 · 드래그: 이동", "Double-click: refresh · Right-click: menu · Drag: move");

    // Menu
    public static string MenuRefresh => T("지금 새로고침", "Refresh now");
    public static string MenuShowOrb => T("원형 위젯 표시", "Show orb");
    public static string MenuSize => T("크기", "Size");
    public static string SizeName(int px) => px switch
    {
        <= 40 => T("아주 작게 (작업 표시줄용)", "Tiny (taskbar)"),
        <= 56 => T("작게", "Small"),
        <= 72 => T("보통", "Medium"),
        <= 96 => T("크게", "Large"),
        _ => T("아주 크게", "Huge"),
    };
    public static string MenuOpacity => T("투명도", "Opacity");
    public static string MenuCenter => T("가운데 숫자", "Center number");
    public static string MenuCenterSession => T("세션 (5시간) 남은 양", "Session (5h) left");
    public static string MenuCenterLowest => T("가장 적게 남은 한도", "Lowest remaining limit");
    public static string MenuPoll => T("새로고침 주기", "Refresh interval");
    public static string PollName(int seconds) => seconds % 60 == 0
        ? T($"{seconds / 60}분", $"{seconds / 60} min")
        : T($"{seconds / 60}분 {seconds % 60}초", $"{seconds / 60} min {seconds % 60} s");
    public static string MenuAlwaysOnTop => T("항상 위에 표시", "Always on top");
    public static string MenuHideFullscreen => T("전체 화면 앱 실행 중엔 숨기기", "Hide while a full-screen app runs");
    public static string MenuLock => T("위치 잠금", "Lock position");
    public static string MenuClickThrough => T("클릭 통과 (트레이 메뉴로 해제)", "Click-through (undo from tray menu)");
    public static string MenuAutoRenew => T("로그인 만료 시 자동 갱신 (사용량 아주 조금 사용)", "Renew expired login automatically (uses a tiny bit of usage)");
    public static string MenuPace => T("시간 경과 표시선", "Pace marker");
    public static string MenuDockTaskbar => T("작업 표시줄 위에 올리기", "Dock onto taskbar");
    public static string MenuResetPosition => T("기본 위치로 (작업 표시줄 옆)", "Reset position (above taskbar)");
    public static string MenuStartup => T("Windows 시작 시 실행", "Start with Windows");
    public static string MenuTrayPromote => T("트레이 아이콘 항상 표시 (숨김 영역 밖으로)", "Always show tray icon (outside overflow)");
    public static string MenuOpenFolder => T("설정 · 로그 폴더 열기", "Open settings / log folder");
    public static string MenuLanguage => T("언어 / Language", "Language / 언어");
    public static string MenuAbout => T("GitHub 페이지", "GitHub page");
    public static string MenuExit => T("종료", "Exit");
}
