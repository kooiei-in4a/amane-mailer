using System.Text;
using System.Text.Encodings.Web;

namespace Amane.Mailer.Admin;

public enum AdminNavItem
{
    MailRequests,
    DeadLetters,
}

public static class AdminLayout
{
    public static void AppendDocumentStart(
        StringBuilder html,
        string title,
        AdminNavItem activeNav,
        int deadLetterCount)
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
        html.AppendLine("    <a class=\"brand-link\" href=\"/admin/mail-requests\">Amane Admin</a>");
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

    private static void AppendSideNav(StringBuilder html, AdminNavItem activeNav, int deadLetterCount)
    {
        html.AppendLine("    <nav class=\"admin-sidenav\" aria-label=\"管理メニュー\">");
        html.AppendLine("      <ul class=\"admin-sidenav-list\">");
        AppendNavItem(html, "/admin/mail-requests", "送信依頼", activeNav == AdminNavItem.MailRequests, badgeCount: null);
        AppendNavItem(
            html,
            "/admin/dead-letters",
            "Dead Letters",
            activeNav == AdminNavItem.DeadLetters,
            badgeCount: deadLetterCount);
        html.AppendLine("      </ul>");
        html.AppendLine("    </nav>");
    }

    private static void AppendNavItem(
        StringBuilder html,
        string href,
        string label,
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
        html.Append(Html(label));
        if (badgeCount is > 0)
        {
            html.Append("<span class=\"nav-badge\" aria-label=\"");
            html.Append(Html(badgeCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            html.Append(" 件");
            html.Append("\">");
            html.Append(Html(badgeCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            html.Append("</span>");
        }

        html.AppendLine("</a>");
        html.AppendLine("        </li>");
    }

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
