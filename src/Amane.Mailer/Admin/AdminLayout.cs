using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

public enum AdminNavItem
{
    MailRequests,
    Senders,
    DeadLetters,
    Suppressions,
    AuditLog,
    Ops,
    SetupStatus,
}

public static class AdminLayout
{
    public static void AppendDocumentStart(
        StringBuilder html,
        string title,
        AdminNavItem activeNav,
        int deadLetterCount,
        AdminTenantAccess? access = null)
    {
        html.AppendLine("""
            <!doctype html>
            <html lang="ja">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
            """);
        html.Append("  <title>");
        html.Append(Html(title));
        html.AppendLine("</title>");
        html.AppendLine("  <link rel=\"stylesheet\" href=\"/admin/admin.css\">");
        html.AppendLine("</head>");
        html.AppendLine("<body class=\"admin-page admin-has-sidenav\">");
        html.AppendLine("  <header class=\"admin-topbar\">");
        html.AppendLine("    <a class=\"brand-link\" href=\"/admin/mail-requests\">");
        html.AppendLine("      <span class=\"brand-mark\" aria-hidden=\"true\">");
        html.AppendLine(Icons.PaperPlane);
        html.AppendLine("      </span>");
        html.AppendLine("      <span class=\"brand-text\">");
        html.AppendLine("        <span class=\"brand-name\">Amane Mailer</span>");
        html.AppendLine("        <span class=\"brand-sub\">Admin Console</span>");
        html.AppendLine("      </span>");
        html.AppendLine("    </a>");
        AppendTopbarEnd(html, access);
        html.AppendLine("  </header>");
        html.AppendLine("  <div class=\"admin-shell\">");
        AppendSideNav(html, activeNav, deadLetterCount);
        html.AppendLine("    <main class=\"admin-main\">");
    }

    public static void AppendDocumentEnd(StringBuilder html)
    {
        html.AppendLine("    </main>");
        html.AppendLine("  </div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
    }

    private static void AppendTopbarEnd(StringBuilder html, AdminTenantAccess? access)
    {
        html.AppendLine("    <div class=\"admin-topbar-end\">");
        if (access is not null)
        {
            var scope = access.IsInstanceOwner || access.IsBreakGlass
                ? "Instance-wide Admin"
                : "Tenant-scoped Admin";
            html.Append("      <span class=\"scope-chip\">");
            html.Append(Html(scope));
            html.AppendLine("</span>");
            var initial = string.IsNullOrWhiteSpace(access.Username)
                ? "A"
                : char.ToUpperInvariant(access.Username.Trim()[0]).ToString();
            html.Append("      <span class=\"admin-avatar\" title=\"");
            html.Append(Html(access.Username));
            html.Append("\">");
            html.Append(Html(initial));
            html.AppendLine("</span>");
        }

        html.AppendLine("    </div>");
    }

    private static void AppendSideNav(StringBuilder html, AdminNavItem activeNav, int deadLetterCount)
    {
        html.AppendLine("    <nav class=\"admin-sidenav\" aria-label=\"管理メニュー\">");
        html.AppendLine("      <ul class=\"admin-sidenav-list\">");
        AppendNavItem(
            html,
            "/admin/mail-requests",
            "送信依頼",
            Icons.PaperPlane,
            activeNav == AdminNavItem.MailRequests,
            badgeCount: null);
        AppendNavItem(html, "/admin/senders", "Senders", Icons.Person, activeNav == AdminNavItem.Senders, badgeCount: null);
        AppendNavItem(
            html,
            "/admin/dead-letters",
            "Dead Letters",
            Icons.Warning,
            activeNav == AdminNavItem.DeadLetters,
            badgeCount: deadLetterCount);
        AppendNavItem(
            html,
            "/admin/suppressions",
            "抑制リスト",
            Icons.Ban,
            activeNav == AdminNavItem.Suppressions,
            badgeCount: null);
        AppendNavItem(html, "/admin/audit-log", "監査ログ", Icons.Clock, activeNav == AdminNavItem.AuditLog, badgeCount: null);
        AppendNavItem(html, "/admin/ops", "運用状況", Icons.Activity, activeNav == AdminNavItem.Ops, badgeCount: null);
        AppendNavItem(
            html,
            "/admin/setup-status",
            "Setup status",
            Icons.Settings,
            activeNav == AdminNavItem.SetupStatus,
            badgeCount: null);
        html.AppendLine("      </ul>");
        html.AppendLine("    </nav>");
    }

    private static void AppendNavItem(
        StringBuilder html,
        string href,
        string label,
        string icon,
        bool isActive,
        int? badgeCount)
    {
        html.Append("        <li class=\"admin-sidenav-item");
        if (isActive)
            html.Append(" is-active");
        html.AppendLine("\">");
        html.Append("          <a class=\"admin-sidenav-link\" href=\"");
        html.Append(Html(href));
        html.Append("\">");
        html.Append("<span class=\"admin-sidenav-icon\" aria-hidden=\"true\">");
        html.Append(icon);
        html.Append("</span>");
        html.Append("<span class=\"admin-sidenav-label\">");
        html.Append(Html(label));
        html.Append("</span>");
        if (badgeCount is > 0)
        {
            html.Append("<span class=\"nav-badge\" aria-label=\"");
            html.Append(Html(badgeCount.Value.ToString(CultureInfo.InvariantCulture)));
            html.Append(" 件");
            html.Append("\">");
            html.Append(Html(badgeCount.Value.ToString(CultureInfo.InvariantCulture)));
            html.Append("</span>");
        }

        html.AppendLine("</a>");
        html.AppendLine("        </li>");
    }

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);

    private static class Icons
    {
        public const string PaperPlane =
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M22 2 11 13"/><path d="M22 2 15 22 11 13 2 9 22 2z"/></svg>""";

        public const string Person =
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="8" r="3.5"/><path d="M5 19.5c.8-3.2 3.4-5 7-5s6.2 1.8 7 5"/></svg>""";

        public const string Warning =
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3 21 20H3L12 3z"/><path d="M12 9v5"/><path d="M12 17h.01"/></svg>""";

        public const string Ban =
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="8"/><path d="m7 7 10 10"/></svg>""";

        public const string Clock =
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="8"/><path d="M12 8v4l3 2"/></svg>""";

        public const string Activity =
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 14h3l2-6 4 10 2-6h5"/></svg>""";

        public const string Settings =
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M12 3v2M12 19v2M4.9 6.5l1.5 1.5M17.6 16l1.5 1.5M3 12h2M19 12h2M4.9 17.5l1.5-1.5M17.6 8l1.5-1.5"/></svg>""";
    }
}
