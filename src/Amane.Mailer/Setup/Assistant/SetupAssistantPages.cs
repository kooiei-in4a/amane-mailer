using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Server-rendered assistant screens. Every dynamic value passes through
/// <see cref="HtmlEncoder"/>, no script is emitted, and no external resource is referenced.
/// Secrets, passwords, provider raw errors, host paths, tokens, and unmasked addresses are never
/// written into a response.
/// </summary>
internal static class SetupAssistantPages
{
    internal const string StyleSheetPath = "/assistant.css";

    internal const string StyleSheet = """
        :root { color-scheme: light; }
        body { font-family: system-ui, sans-serif; margin: 0; background: #f5f6f8; color: #1b1f24; }
        main { max-width: 46rem; margin: 0 auto; padding: 1.5rem 1rem 4rem; }
        h1 { font-size: 1.25rem; margin: 0 0 .25rem; }
        h2 { font-size: 1.05rem; margin: 1.5rem 0 .5rem; }
        .stage { font-size: .85rem; color: #52606d; margin: 0 0 1rem; }
        .badge { display: inline-block; border-radius: .75rem; padding: .1rem .6rem; font-size: .75rem; background: #dfe3e8; }
        .card { background: #fff; border: 1px solid #d7dbe0; border-radius: .5rem; padding: 1rem 1.25rem; margin-bottom: 1rem; }
        .state { border-left: .3rem solid #99a; padding-left: .75rem; }
        .state-ok { border-left-color: #2e7d32; }
        .state-fail { border-left-color: #b3261e; }
        .state-action { border-left-color: #b26a00; }
        .state-manual { border-left-color: #6a1b9a; }
        label { display: block; margin: .75rem 0 .25rem; font-size: .9rem; }
        input[type=text], input[type=password], input[type=email] { width: 100%; box-sizing: border-box; padding: .45rem .5rem; border: 1px solid #b8bfc7; border-radius: .25rem; font-size: .95rem; }
        fieldset { border: 1px solid #d7dbe0; border-radius: .4rem; margin: .75rem 0; }
        legend { font-size: .85rem; padding: 0 .35rem; }
        button { font-size: .95rem; padding: .5rem 1rem; border-radius: .3rem; border: 1px solid #2a4b8d; background: #2a4b8d; color: #fff; cursor: pointer; }
        button.secondary { background: #fff; color: #2a4b8d; }
        .actions { display: flex; gap: .75rem; flex-wrap: wrap; margin-top: 1rem; }
        .rejection { background: #fdecea; border: 1px solid #f2b8b5; border-radius: .3rem; padding: .6rem .8rem; margin-bottom: 1rem; }
        .note { font-size: .85rem; color: #52606d; }
        dl { display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; margin: .5rem 0; font-size: .9rem; }
        dt { color: #52606d; }
        dd { margin: 0; }
        ul { margin: .4rem 0; padding-left: 1.2rem; font-size: .9rem; }
        """;

    internal static string RenderLanding(string? rejectionText)
    {
        var body = new StringBuilder();
        body.AppendLine("<div class=\"card\">");
        body.AppendLine("<p>ターミナルに表示された一度きりのトークンを貼り付けてください。</p>");
        body.AppendLine("<p class=\"note\">トークンは URL に含めません。ブラウザーの履歴や Referer に残らないよう、必ずこのフォームから送信してください。</p>");
        body.AppendLine("<form method=\"post\" action=\"/token\" autocomplete=\"off\">");
        body.AppendLine("<label for=\"one_time_token\">One-time token</label>");
        body.AppendLine("<input id=\"one_time_token\" name=\"one_time_token\" type=\"password\" autocomplete=\"off\" required>");
        body.AppendLine("<div class=\"actions\"><button type=\"submit\">開始する</button></div>");
        body.AppendLine("</form>");
        body.AppendLine("</div>");
        return Document("Amane Mailer Easy Setup", null, rejectionText, body.ToString());
    }

    internal static string Render(SetupAssistantSession session)
    {
        var body = session.Step switch
        {
            SetupAssistantStep.Welcome => RenderWelcome(session),
            SetupAssistantStep.DockerPreflight => RenderDockerPreflight(session),
            SetupAssistantStep.ModeSelection => RenderModeSelection(session),
            SetupAssistantStep.TenantBasics => RenderTenantBasics(session),
            SetupAssistantStep.ProviderSettings => RenderProviderSettings(session),
            SetupAssistantStep.AcsSettings => RenderAcsSettings(session),
            SetupAssistantStep.ApplyConfirmation => RenderApplyConfirmation(session),
            SetupAssistantStep.ApplyOutcome => RenderApplyOutcome(session),
            SetupAssistantStep.DeploymentVerification => RenderDeploymentVerification(session),
            SetupAssistantStep.MainSetupComplete => RenderMainSetupComplete(session),
            SetupAssistantStep.AdminChoice => RenderAdminChoice(session),
            SetupAssistantStep.AdminAccessPreflight => RenderAdminAccessPreflight(session),
            SetupAssistantStep.AdminBootstrapOutcome => RenderAdminBootstrap(session),
            SetupAssistantStep.FinalGuidance => RenderFinalGuidance(session),
            SetupAssistantStep.ManualModeGuidance => RenderManualGuidance(session),
            _ => RenderCancelled(),
        };

        if (session.Step is not (SetupAssistantStep.Cancelled or SetupAssistantStep.FinalGuidance))
        {
            body += CancelForm(session);
        }

        return Document(
            SetupAssistantStepInfo.Title(session.Step),
            session.Step,
            SetupAssistantResultPresenter.DescribeRejection(session.InputRejectionKey),
            body);
    }

    private static string RenderWelcome(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        body.AppendLine("<div class=\"card\">");
        body.AppendLine("<p>この Assistant は host 上の localhost 限定 Web UI です。通常の Mailer runtime とは分離して動作します。</p>");
        body.AppendLine("<h2>対応範囲</h2>");
        body.AppendLine("<ul>");
        body.AppendLine("<li>mode 1〜4 の Main setup（Local Mailpit / Staging / Production ACS）</li>");
        body.AppendLine("<li>Main setup 成功後の任意 Admin bootstrap</li>");
        body.AppendLine("</ul>");
        body.AppendLine("<h2>対応しないこと</h2>");
        body.AppendLine("<ul>");
        body.AppendLine("<li>mode 5（production ACS + Queue）の自動設定。Manual runbook の案内のみ行います。</li>");
        body.AppendLine("<li>reverse proxy・証明書・DNS の自動構築</li>");
        body.AppendLine("<li>実送信による運用確認の記録</li>");
        body.AppendLine("</ul>");
        body.AppendLine("<p class=\"note\">この画面は JavaScript を使用しません。JavaScript を無効にしたままでもすべての操作が完了できます。</p>");
        body.AppendLine("</div>");
        body.Append(Form(session, "/welcome", "Docker preflight へ進む"));
        return body.ToString();
    }

    private static string RenderDockerPreflight(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        var preflight = session.DockerPreflight;
        if (preflight is null)
        {
            body.Append(Card("Docker preflight の結果がありません。"));
        }
        else
        {
            body.Append(StateCard(
                preflight.Passed ? SetupAssistantOutcomeKind.Succeeded : SetupAssistantOutcomeKind.Failed,
                SetupAssistantResultPresenter.Describe(preflight.Code),
                new (string, string)[]
                {
                    ("結果コード", SetupAssistantResultPresenter.SafeCode(preflight.Code)),
                    ("Docker engine", SetupAssistantResultPresenter.SafeLabel(preflight.EngineKind, "未判定")),
                }));
        }

        body.Append(Form(session, "/preflight", "再実行する", secondary: true));
        if (preflight is { Passed: true })
        {
            body.Append(Form(session, "/preflight", "Setup mode 選択へ進む", hidden: ("action", "continue")));
        }

        return body.ToString();
    }

    private static string RenderModeSelection(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        body.AppendLine("<form method=\"post\" action=\"/mode\" class=\"card\" autocomplete=\"off\">");
        body.AppendLine(CsrfField(session));
        body.AppendLine("<fieldset><legend>Setup mode</legend>");
        body.Append(ModeRadio("local-mailpit", "mode 1: Local Mailpit（develop・実送信なし）"));
        body.Append(ModeRadio("staging-no-send", "mode 2: Staging ACS（送信検証を行わない）"));
        body.Append(ModeRadio("staging-verification", "mode 3: Staging ACS（Staging verification を行う）"));
        body.Append(ModeRadio("production-acs", "mode 4: Production ACS（live_sending 有効化まで）"));
        body.Append(ModeRadio(SetupAssistantInputs.ManualModeValue, "mode 5: Production ACS + Queue（Manual runbook 案内のみ）"));
        body.AppendLine("</fieldset>");
        body.AppendLine("<div class=\"actions\"><button type=\"submit\">選択して進む</button></div>");
        body.AppendLine("</form>");
        return body.ToString();
    }

    private static string RenderTenantBasics(SetupAssistantSession session)
    {
        var mode = session.Mode ?? SetupMode.LocalMailpit;
        var body = new StringBuilder();
        body.AppendLine("<form method=\"post\" action=\"/tenant\" class=\"card\" autocomplete=\"off\">");
        body.AppendLine(CsrfField(session));
        body.Append(Summary(
            ("Setup mode", SetupModeParser.ToWireValue(mode)),
            ("environment", SetupAssistantInputs.EnvironmentFor(mode)),
            ("tenant_id", session.TenantId.ToString("D"))));
        body.Append(TextField("tenant_name", "Tenant 名", "英数字・ハイフン・アンダースコア"));
        body.Append(TextField("source_service", "Source service 名", "呼び出し元サービスの識別子"));
        body.Append(TextField("sender_email", "既定の送信元アドレス", "例: no-reply@example.com", type: "email"));
        body.Append(TextField("sender_display_name", "送信元表示名", null));
        body.AppendLine("<div class=\"actions\"><button type=\"submit\">Provider 設定へ進む</button></div>");
        body.AppendLine("</form>");
        return body.ToString();
    }

    private static string RenderProviderSettings(SetupAssistantSession session)
    {
        var mode = session.Mode ?? SetupMode.LocalMailpit;
        var body = new StringBuilder();
        body.AppendLine("<form method=\"post\" action=\"/provider\" class=\"card\" autocomplete=\"off\">");
        body.AppendLine(CsrfField(session));
        body.Append(Summary(
            ("provider", SetupAssistantInputs.ProviderFor(mode)),
            ("token_env", SetupAssistantInputs.TokenEnvFor(mode)),
            ("live_sending", "false（初回適用は常に無効）")));
        body.AppendLine("<p class=\"note\">provider と token_env は mode から決まります。UI からは変更できません。</p>");
        body.Append(TextField(
            "service_token",
            "Mailer service token",
            $"{SetupAssistantInputs.MinSecretLength} 文字以上",
            type: "password"));
        body.Append(TextField("service_token_confirm", "Mailer service token（確認）", null, type: "password"));
        body.AppendLine("<div class=\"actions\"><button type=\"submit\">次へ進む</button></div>");
        body.AppendLine("</form>");
        return body.ToString();
    }

    private static string RenderAcsSettings(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        body.AppendLine("<form method=\"post\" action=\"/acs\" class=\"card\" autocomplete=\"off\">");
        body.AppendLine(CsrfField(session));
        body.AppendLine("<p class=\"note\">接続文字列は入力欄以外へ保存されません。画面へ再表示することもありません。</p>");
        body.Append(TextField("acs_connection_string", "ACS connection string", null, type: "password"));
        body.Append(TextField("acs_connection_string_confirm", "ACS connection string（確認）", null, type: "password"));
        body.Append(TextField("platform_sender_display_name", "Platform sender 表示名", null));
        body.AppendLine("<div class=\"actions\"><button type=\"submit\">適用前確認へ進む</button></div>");
        body.AppendLine("</form>");
        return body.ToString();
    }

    private static string RenderApplyConfirmation(SetupAssistantSession session)
    {
        var mode = session.Mode ?? SetupMode.LocalMailpit;
        var body = new StringBuilder();
        body.AppendLine("<div class=\"card\">");
        body.AppendLine("<p>次の内容で設定 bundle を生成し、コンテナを再作成します。この操作は既存の構成に影響します。</p>");
        body.Append(Summary(
            ("Setup mode", SetupModeParser.ToWireValue(mode)),
            ("environment", SetupAssistantInputs.EnvironmentFor(mode)),
            ("provider", SetupAssistantInputs.ProviderFor(mode)),
            ("tenant_id", session.TenantId.ToString("D")),
            ("送信元（マスク表示）", SetupAssistantInputs.Mask(session.SenderEmail)),
            ("service token", "入力済み（表示しません）"),
            ("ACS connection string", session.AcsConnectionString is null ? "未使用" : "入力済み（表示しません）")));
        body.AppendLine("</div>");

        body.AppendLine("<form method=\"post\" action=\"/confirm\" class=\"card\" autocomplete=\"off\">");
        body.AppendLine(CsrfField(session));
        if (mode != SetupMode.LocalMailpit)
        {
            body.AppendLine("<p class=\"note\">次の 2 つのフレーズを大文字・小文字を含めて正確に入力してください。</p>");
            body.Append(TextField(
                "environment_confirmation",
                $"環境確認フレーズ（{SetupAssistantInputs.EnvironmentConfirmationFor(mode)}）",
                null));
            body.Append(TextField(
                "intent_confirmation",
                $"実行意図フレーズ（{AcsRegisterOperation.IntentPhrase}）",
                null));
        }

        body.AppendLine("<div class=\"actions\"><button type=\"submit\">適用を実行する</button></div>");
        body.AppendLine("</form>");
        return body.ToString();
    }

    private static string RenderApplyOutcome(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        var outcome = session.MainSetup;
        if (outcome is null)
        {
            return Card("適用結果がありません。");
        }

        body.Append(ApplyOutcomeCard(outcome));
        if (outcome.Kind == SetupAssistantOutcomeKind.Succeeded)
        {
            body.Append(Form(session, "/verify", "次の段階へ進む", hidden: ("action", "continue")));
        }
        else if (outcome.Kind is SetupAssistantOutcomeKind.Rejected or SetupAssistantOutcomeKind.Failed)
        {
            body.Append(Form(session, "/confirm", "適用をやり直す", secondary: true, hidden: ("action", "retry")));
        }

        return body.ToString();
    }

    private static string RenderDeploymentVerification(SetupAssistantSession session)
    {
        var mode = session.Mode ?? SetupMode.LocalMailpit;
        var body = new StringBuilder();

        if (session.Staging is { } staging)
        {
            body.Append(StateCard(
                staging.Kind,
                SetupAssistantResultPresenter.Describe(staging.Code),
                new (string, string)[]
                {
                    ("結果コード", SetupAssistantResultPresenter.SafeCode(staging.Code)),
                    ("送信要求受理", staging.SendRequestAccepted ? "はい" : "いいえ"),
                    ("受信箱の確認", SetupAssistantResultPresenter.SafeLabel(staging.MailboxCheckStatus, "未実施（手動確認が必要）")),
                    ("送信元（マスク表示）", staging.MaskedSenderEmail ?? "-"),
                    ("宛先（マスク表示）", staging.MaskedRecipientEmail ?? "-"),
                }));
            body.Append(Form(session, "/verify", "Main setup 完了へ進む", hidden: ("action", "finish")));
            return body.ToString();
        }

        switch (mode)
        {
            case SetupMode.StagingVerification:
                body.AppendLine("<form method=\"post\" action=\"/verify\" class=\"card\" autocomplete=\"off\">");
                body.AppendLine(CsrfField(session));
                body.AppendLine(HiddenField("action", "staging"));
                body.AppendLine("<p>Staging verification のテスト送信を行います。宛先は同一 session 内でのみ保持し、マスク表示のみ行います。</p>");
                body.Append(TextField("recipient_email", "テスト送信の宛先", null, type: "email"));
                body.Append(TextField(
                    "environment_confirmation",
                    $"環境確認フレーズ（{AcsEnvironmentConfirmation.Staging}）",
                    null));
                body.Append(TextField(
                    "intent_confirmation",
                    $"実行意図フレーズ（{AcsStagingVerificationOperation.IntentPhrase}）",
                    null));
                body.AppendLine("<div class=\"actions\"><button type=\"submit\">テスト送信を実行する</button></div>");
                body.AppendLine("</form>");
                break;

            case SetupMode.ProductionAcs:
                body.AppendLine("<form method=\"post\" action=\"/verify\" class=\"card\" autocomplete=\"off\">");
                body.AppendLine(CsrfField(session));
                body.AppendLine(HiddenField("action", "production"));
                body.AppendLine("<p>live_sending を有効化します。実送信はこの Assistant では行いません。</p>");
                body.Append(TextField(
                    "environment_confirmation",
                    $"環境確認フレーズ（{AcsEnvironmentConfirmation.Production}）",
                    null));
                body.Append(TextField(
                    "live_sending_approval",
                    $"有効化承認フレーズ（{AcsLiveSendingApproval.EnablePhrase}）",
                    null));
                body.AppendLine("<div class=\"actions\"><button type=\"submit\">live_sending を有効化する</button></div>");
                body.AppendLine("</form>");
                break;

            default:
                body.Append(Card(mode == SetupMode.LocalMailpit
                    ? "Local Mailpit mode では追加の送信検証は行いません。"
                    : "この mode では Staging verification を行いません。"));
                body.Append(Form(session, "/verify", "Main setup 完了へ進む", hidden: ("action", "finish")));
                break;
        }

        return body.ToString();
    }

    private static string RenderMainSetupComplete(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        var outcome = session.MainSetup;
        body.Append(StateCard(
            SetupAssistantOutcomeKind.Succeeded,
            "Main setup は完了しました。",
            new (string, string)[]
            {
                ("Main setup", "成功"),
                ("設定の適用", outcome?.ConfigurationApplied == true ? "適用済み" : "未適用"),
                ("Staging verification", DescribeStagingState(session)),
                ("Deployment send-ready", outcome?.DeploymentSendReady == true ? "到達" : "未到達"),
                ("実送信による運用確認", "記録していません。必要な場合は Manual verification を実施してください。"),
            }));
        body.AppendLine("<div class=\"card note\">Production mode の通常完了はここまでです。実送信による運用確認や、リリース判定に必要な確認は、この Assistant では行っておらず、記録もしていません。</div>");
        body.Append(Form(session, "/admin-choice", "Admin 有効化の選択へ進む", hidden: ("action", "open")));
        body.Append(Form(session, "/finish", "Admin を設定せず終了する", secondary: true, hidden: ("action", "skip")));
        return body.ToString();
    }

    private static string RenderAdminChoice(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        body.AppendLine("<div class=\"card\">");
        body.AppendLine("<p>Admin は既定で無効です。有効化は Main setup とは独立した任意の transaction として実行します。</p>");
        body.AppendLine("<ul>");
        body.AppendLine("<li>Admin bootstrap が失敗しても Main setup の成功は取り消されません。</li>");
        body.AppendLine("<li>access profile が成立しない場合、Admin は無効のまま維持されます。</li>");
        body.AppendLine("<li>設定の巻き戻しと Admin の SQLite 副作用の巻き戻しは同一ではありません。</li>");
        body.AppendLine("</ul>");
        body.AppendLine("</div>");
        body.Append(Form(session, "/admin-preflight", "Admin を有効化する", hidden: ("action", "open")));
        body.Append(Form(session, "/finish", "Admin を有効化せず終了する", secondary: true, hidden: ("action", "skip")));
        return body.ToString();
    }

    private static string RenderAdminAccessPreflight(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        if (session.AdminPreflight is { } preflight)
        {
            body.Append(StateCard(
                preflight.Satisfied ? SetupAssistantOutcomeKind.Succeeded : SetupAssistantOutcomeKind.Rejected,
                preflight.Satisfied
                    ? "Admin access profile の形式確認に成功しました。最終的な判定は bootstrap 実行時に行われます。"
                    : "Admin access profile の条件を満たしていません。Admin は無効のまま維持します。",
                new (string, string)[]
                {
                    ("access profile", preflight.Profile.ToString()),
                    ("理由コード", SetupAssistantResultPresenter.SafeCode(preflight.ReasonCode)),
                }));
            if (preflight.Satisfied)
            {
                body.Append(Form(session, "/admin-bootstrap", "Admin bootstrap へ進む", hidden: ("action", "open")));
            }

            body.Append(Form(session, "/finish", "Admin を有効化せず終了する", secondary: true, hidden: ("action", "skip")));
            return body.ToString();
        }

        body.AppendLine("<form method=\"post\" action=\"/admin-preflight\" class=\"card\" autocomplete=\"off\">");
        body.AppendLine(CsrfField(session));
        body.AppendLine("<fieldset><legend>Access profile</legend>");
        body.AppendLine("<label><input type=\"radio\" name=\"profile\" value=\"local-development\" required> Local Development（loopback・HTTP 許可）</label>");
        body.AppendLine("<label><input type=\"radio\" name=\"profile\" value=\"production-https\"> Production HTTPS（承認済み reverse proxy が既に存在）</label>");
        body.AppendLine("</fieldset>");
        body.Append(TextField("origin", "Admin の接続元 URL", "例: http://127.0.0.1:5280/"));
        body.Append(TextField("environment_name", "ASPNETCORE_ENVIRONMENT", "Development / Staging / Production"));
        body.Append(TextField("allowed_local_address", "Admin 到達を許可する server local address", "例: 127.0.0.1"));
        body.AppendLine("<fieldset><legend>事前条件の確認</legend>");
        body.AppendLine("<label><input type=\"checkbox\" name=\"loopback_only_published\" value=\"true\"> host へ公開しているのは loopback port だけである</label>");
        body.AppendLine("<label><input type=\"checkbox\" name=\"approved_reverse_proxy\" value=\"true\"> 承認済みの HTTPS reverse proxy 経路が既に存在する</label>");
        body.AppendLine("<label><input type=\"checkbox\" name=\"server_local_address_confirmed\" value=\"true\"> server 側 local address を確認済みである</label>");
        body.AppendLine("</fieldset>");
        body.AppendLine("<p class=\"note\">Easy Setup は reverse proxy・証明書・DNS を構築しません。既存の経路のみを判定します。</p>");
        body.AppendLine("<div class=\"actions\"><button type=\"submit\">preflight を実行する</button></div>");
        body.AppendLine("</form>");
        return body.ToString();
    }

    private static string RenderAdminBootstrap(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        if (session.AdminBootstrap is { } outcome)
        {
            body.Append(StateCard(
                outcome.Kind,
                SetupAssistantResultPresenter.Describe(outcome.Code),
                new (string, string)[]
                {
                    ("結果コード", SetupAssistantResultPresenter.SafeCode(outcome.Code)),
                    ("access profile", SetupAssistantResultPresenter.SafeCode(outcome.AccessProfile)),
                    ("設定の巻き戻し", SetupAssistantResultPresenter.SafeCode(outcome.ConfigRollback)),
                    ("Admin データベース状態", SetupAssistantResultPresenter.SafeCode(outcome.AdminDatabaseState)),
                    ("Admin 到達性", SetupAssistantResultPresenter.SafeCode(outcome.AdminExposure)),
                    ("login 確認", SetupAssistantResultPresenter.SafeCode(outcome.LoginVerification)),
                    ("状態画面の確認", SetupAssistantResultPresenter.SafeCode(outcome.SetupStatusVerification)),
                    ("検証 session の後始末", SetupAssistantResultPresenter.SafeCode(outcome.VerificationSessionCleanup)),
                    ("手動対応", outcome.ManualActionRequired ? "必要" : "不要"),
                }));
            body.AppendLine("<div class=\"card note\">Admin bootstrap の結果は Main setup とは独立しています。ここが失敗しても Main setup の成功は維持されます。</div>");
            body.Append(Form(session, "/finish", "最終案内へ進む", hidden: ("action", "continue")));
            return body.ToString();
        }

        body.AppendLine("<form method=\"post\" action=\"/admin-bootstrap\" class=\"card\" autocomplete=\"off\">");
        body.AppendLine(CsrfField(session));
        body.AppendLine("<p>Admin の初期認証情報を設定します。password はこの session のメモリ内でのみ扱い、画面へ再表示しません。</p>");
        body.Append(TextField("admin_username", "Admin ユーザー名", "英数字・ハイフン・アンダースコア"));
        body.Append(TextField(
            "admin_password",
            "Admin password",
            $"{SetupAssistantInputs.MinSecretLength} 文字以上",
            type: "password"));
        body.Append(TextField("admin_password_confirm", "Admin password（確認）", null, type: "password"));
        body.AppendLine("<div class=\"actions\"><button type=\"submit\">Admin bootstrap を実行する</button></div>");
        body.AppendLine("</form>");
        return body.ToString();
    }

    private static string RenderFinalGuidance(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        body.Append(StateCard(
            SetupAssistantOutcomeKind.Succeeded,
            "Easy Setup を終了します。",
            new (string, string)[]
            {
                ("Main setup", session.MainSetupSucceeded ? "成功" : "未完了"),
                ("Staging verification", DescribeStagingState(session)),
                ("Deployment send-ready", session.MainSetup?.DeploymentSendReady == true ? "到達" : "未到達"),
                ("Admin bootstrap", DescribeAdminState(session)),
                ("実送信による運用確認", "記録していません"),
            }));
        body.AppendLine("<div class=\"card\">");
        body.AppendLine("<h2>次に確認する場所</h2>");
        body.AppendLine("<ul>");
        body.AppendLine("<li>Admin を有効化した場合は、Admin の setup status 画面で現在の構成を確認できます。</li>");
        body.AppendLine("<li>Admin を有効化していない場合や mode 5 を利用する場合は、配布物に含まれる setup runbook を参照してください。</li>");
        body.AppendLine("</ul>");
        body.AppendLine("</div>");
        body.Append(Form(session, "/finish", "Assistant を終了する", hidden: ("action", "stop")));
        return body.ToString();
    }

    private static string RenderManualGuidance(SetupAssistantSession session)
    {
        var body = new StringBuilder();
        body.AppendLine("<div class=\"card\">");
        body.AppendLine("<p>mode 5（production ACS + Queue）は Easy Setup の自動化対象外です。この Assistant は設定を変更しません。</p>");
        body.AppendLine("<h2>手順</h2>");
        body.AppendLine("<ul>");
        body.AppendLine("<li>配布物に含まれる setup runbook と bounce ingestion runbook に従って手動で構成してください。</li>");
        body.AppendLine("<li>Queue と bounce の設定は Manual Deployment の契約のまま維持されます。</li>");
        body.AppendLine("</ul>");
        body.AppendLine("</div>");
        body.Append(Form(session, "/mode", "mode 選択へ戻る", secondary: true, hidden: ("action", "back")));
        body.Append(Form(session, "/finish", "Assistant を終了する", secondary: true, hidden: ("action", "stop")));
        return body.ToString();
    }

    private static string RenderCancelled() =>
        Card("Assistant を終了しました。このタブは閉じてかまいません。ローカルサーバーは停止済みです。");

    internal static string RenderTerminated(string reasonText) =>
        Document("Amane Mailer Easy Setup", null, null, Card(reasonText));

    private static string DescribeStagingState(SetupAssistantSession session) =>
        session.Staging is null
            ? "未実施"
            : session.Staging.Kind == SetupAssistantOutcomeKind.Succeeded ? "送信要求受理" : "失敗";

    private static string DescribeAdminState(SetupAssistantSession session)
    {
        if (session.AdminSkipped)
        {
            return "実行していません（skip）";
        }

        return session.AdminBootstrap is null
            ? "未実施"
            : session.AdminBootstrap.Kind == SetupAssistantOutcomeKind.Succeeded ? "成功" : "失敗（Admin は無効のまま）";
    }

    private static string ApplyOutcomeCard(SetupAssistantMainSetupOutcome outcome)
    {
        var rows = new List<(string, string)>
        {
            ("結果コード", SetupAssistantResultPresenter.SafeCode(outcome.Code)),
            ("設定の適用", outcome.ConfigurationApplied ? "適用済み" : "未適用"),
            ("Deployment send-ready", outcome.DeploymentSendReady ? "到達" : "未到達"),
            ("設定の巻き戻し", SetupAssistantResultPresenter.SafeLabel(outcome.ConfigRollbackStatus, "not-applicable")),
        };

        if (outcome.PersistentSideEffectMayRemain)
        {
            rows.Add(("残存する副作用", SetupAssistantResultPresenter.SafeLabel(outcome.PersistentSideEffectKind, "unknown")));
        }

        var text = SetupAssistantResultPresenter.Describe(outcome.Code);
        var action = SetupAssistantResultPresenter.DescribeAction(outcome.ActionCode);
        if (!string.IsNullOrEmpty(action))
        {
            rows.Add(("必要な対応", action));
        }

        return StateCard(outcome.Kind, text, rows);
    }

    private static string ModeRadio(string value, string label) =>
        $"<label><input type=\"radio\" name=\"mode\" value=\"{Enc(value)}\" required> {Enc(label)}</label>\n";

    private static string TextField(
        string name,
        string label,
        string? hint,
        string type = "text")
    {
        var builder = new StringBuilder();
        builder.Append("<label for=\"").Append(Enc(name)).Append("\">").Append(Enc(label));
        if (!string.IsNullOrEmpty(hint))
        {
            builder.Append(" <span class=\"note\">").Append(Enc(hint)).Append("</span>");
        }

        builder.AppendLine("</label>");

        // Values are never re-rendered into the markup, so a refresh or browser back never
        // restores a secret, password, sender, or recipient the operator typed earlier.
        builder.Append("<input id=\"").Append(Enc(name))
            .Append("\" name=\"").Append(Enc(name))
            .Append("\" type=\"").Append(Enc(type))
            .AppendLine("\" autocomplete=\"off\" required>");
        return builder.ToString();
    }

    private static string HiddenField(string name, string value) =>
        $"<input type=\"hidden\" name=\"{Enc(name)}\" value=\"{Enc(value)}\">";

    /// <summary>
    /// Always-available exit. Cancelling discards the session, including every secret and address
    /// the operator entered, and stops the local server.
    /// </summary>
    private static string CancelForm(SetupAssistantSession session) =>
        "<p class=\"note\">中止すると入力内容は破棄され、ローカルサーバーは停止します。"
        + "すでに適用済みの設定はここでは元に戻りません。</p>\n"
        + Form(session, "/cancel", "Assistant を中止する", secondary: true);

    private static string CsrfField(SetupAssistantSession session) =>
        HiddenField(SetupAssistantSecurity.CsrfFieldName, session.CsrfToken);

    private static string Form(
        SetupAssistantSession session,
        string action,
        string label,
        bool secondary = false,
        (string Name, string Value)? hidden = null)
    {
        var builder = new StringBuilder();
        builder.Append("<form method=\"post\" action=\"").Append(Enc(action)).AppendLine("\">");
        builder.AppendLine(CsrfField(session));
        if (hidden is { } field)
        {
            builder.AppendLine(HiddenField(field.Name, field.Value));
        }

        builder.Append("<div class=\"actions\"><button type=\"submit\"")
            .Append(secondary ? " class=\"secondary\"" : string.Empty)
            .Append('>').Append(Enc(label)).AppendLine("</button></div>");
        builder.AppendLine("</form>");
        return builder.ToString();
    }

    private static string Card(string text) =>
        $"<div class=\"card\"><p>{Enc(text)}</p></div>\n";

    private static string Summary(params (string Label, string Value)[] rows) =>
        RenderDefinitionList(rows);

    private static string StateCard(
        SetupAssistantOutcomeKind kind,
        string text,
        IReadOnlyList<(string Label, string Value)> rows)
    {
        var cssClass = kind switch
        {
            SetupAssistantOutcomeKind.Succeeded => "state state-ok",
            SetupAssistantOutcomeKind.ActionRequired => "state state-action",
            SetupAssistantOutcomeKind.ManualInterventionRequired => "state state-manual",
            _ => "state state-fail",
        };

        var builder = new StringBuilder();
        builder.Append("<div class=\"card ").Append(cssClass).AppendLine("\">");
        builder.Append("<p><strong>").Append(Enc(KindLabel(kind))).Append("</strong> ")
            .Append(Enc(text)).AppendLine("</p>");
        builder.Append(RenderDefinitionList(rows));
        builder.AppendLine("</div>");
        return builder.ToString();
    }

    private static string KindLabel(SetupAssistantOutcomeKind kind) => kind switch
    {
        SetupAssistantOutcomeKind.Succeeded => "[成功]",
        SetupAssistantOutcomeKind.Rejected => "[入力却下]",
        SetupAssistantOutcomeKind.Failed => "[FAIL]",
        SetupAssistantOutcomeKind.ActionRequired => "[ACTION]",
        SetupAssistantOutcomeKind.ManualInterventionRequired => "[手動対応が必要]",
        _ => "[結果]",
    };

    private static string RenderDefinitionList(IReadOnlyList<(string Label, string Value)> rows)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("<dl>\n");
        foreach (var (label, value) in rows)
        {
            builder.Append("<dt>").Append(Enc(label)).AppendLine("</dt>");
            builder.Append("<dd>").Append(Enc(value)).AppendLine("</dd>");
        }

        builder.AppendLine("</dl>");
        return builder.ToString();
    }

    private static string Document(
        string title,
        SetupAssistantStep? step,
        string? rejectionText,
        string body)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"ja\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<meta name=\"referrer\" content=\"no-referrer\">");
        html.Append("<title>").Append(Enc(title)).AppendLine(" - Amane Mailer Easy Setup</title>");
        html.Append("<link rel=\"stylesheet\" href=\"").Append(StyleSheetPath).AppendLine("\">");
        html.AppendLine("</head>");
        html.AppendLine("<body><main>");
        html.Append("<h1>").Append(Enc(title)).AppendLine("</h1>");

        if (step is { } current && current <= SetupAssistantStep.FinalGuidance)
        {
            var transaction = SetupAssistantStepInfo.TransactionOf(current)
                == SetupAssistantTransaction.MainSetup
                ? "Main setup transaction"
                : "Admin bootstrap transaction";
            html.Append("<p class=\"stage\"><span class=\"badge\">").Append(Enc(transaction))
                .Append("</span> ステップ ")
                .Append(SetupAssistantStepInfo.DisplayNumber(current))
                .Append(" / ")
                .Append(SetupAssistantStepInfo.TotalSteps)
                .AppendLine("</p>");
        }

        if (!string.IsNullOrEmpty(rejectionText))
        {
            html.Append("<div class=\"rejection\">").Append(Enc(rejectionText)).AppendLine("</div>");
        }

        html.AppendLine(body);
        html.AppendLine("</main></body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>
    /// Encodes for HTML text and attribute contexts. Japanese is left as literal UTF-8 because the
    /// document declares that charset; every HTML-significant character is still escaped, so no
    /// operator-supplied value can break out of the surrounding element or attribute.
    /// </summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static string Enc(string value) => Encoder.Encode(value);
}
