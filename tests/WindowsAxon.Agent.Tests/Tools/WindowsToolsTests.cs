using System.Text.Json;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.SubAgents;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Agent.Undo;
using WindowsAxon.Windows;
using NSubstitute;

namespace WindowsAxon.Agent.Tests.Tools;

public sealed class WindowsToolsTests : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(),
                                                   "windowsaxon-tools-test-" + Guid.NewGuid().ToString("N"));

    private UndoJournal Journal => new(Path.Combine(_folder, "undo"), TimeProvider.System);

    public WindowsToolsTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void Arithmetic_rejects_null_expression_with_its_parameter_name()
    {
        var error = Assert.Throws<ArgumentNullException>(() => Arithmetic.Evaluate(null!));

        Assert.Equal("expression", error.ParamName);
    }

    [Fact]
    public void Registry_validation_rejects_null_path_with_its_parameter_name()
    {
        var error = Assert.Throws<ArgumentNullException>(() => RegistryTools.ValidatePath(null!, write: false));

        Assert.Equal("path", error.ParamName);
    }

    [Theory]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("-2^2", -4)]
    [InlineData("2^3^2", 512)]
    [InlineData("10 % 3", 1)]
    public void Arithmetic_obeys_precedence(string expression, double expected) => Assert.Equal(expected,
                                                                                                Arithmetic.Evaluate(expression));

    [Theory]
    [InlineData("1; System.Diagnostics.Process.Start('cmd')")]
    [InlineData("(1 + 2")]
    [InlineData("1/0")]
    [InlineData("NaN")]
    public void Arithmetic_rejects_code_and_invalid_results(string expression) => Assert.ThrowsAny<Exception>(() => Arithmetic.Evaluate(expression));

    [Fact]
    public void File_operations_preserve_existing_destination_and_limit_reads()
    {
        var tools = new FileTools(Journal, "test");
        var source = Path.Combine(_folder, "source.txt");
        var copy = Path.Combine(_folder, "copy.txt");
        File.WriteAllText(source, new string('x', 25000));
        tools.Copy(source, copy);
        Assert.Throws<IOException>(() => tools.Copy(source, copy));
        Assert.StartsWith(new string('x', 20 * 1024), tools.Read(copy));
        var moved = Path.Combine(_folder, "moved.txt");
        tools.Move(copy, moved);
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public void Binary_and_credential_files_are_refused()
    {
        var tools = new FileTools(Journal, "test");
        var binary = Path.Combine(_folder, "binary.bin");
        File.WriteAllBytes(binary, [1, 0, 2]);
        Assert.Throws<ArgumentException>(() => tools.Read(binary));
        foreach (var path in new[] {
            Path.Combine(_folder, ".ssh", "id_rsa"),
            Path.Combine(_folder, "secret.pfx"),
            Path.Combine(_folder,
                         ".env"),
            Path.Combine(_folder,
                         ".env::$DATA")
        })
        {
            Assert.Throws<UnauthorizedAccessException>(() => FileTools.SafeReadPath(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_read_limit_does_not_split_a_multibyte_character(bool utf16)
    {
        var path = Path.Combine(_folder, "chinese.txt");
        File.WriteAllText(path,
                          new string('\u4e2d', 30000),
                          utf16 ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8);
        var text = new FileTools(Journal, "test").Read(path);
        Assert.Contains("cut at 20 KB", text);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.StartsWith(new string('\u4e2d', 6000), text);
    }

    [Fact]
    public void Search_uses_filename_patterns()
    {
        var tools = new FileTools(Journal, "test");
        File.WriteAllText(Path.Combine(_folder, "one.txt"), "one");
        File.WriteAllText(Path.Combine(_folder, "two.bin"), "two");
        var result = JsonSerializer.SerializeToElement(tools.Find(_folder,
                                                                  "*.txt",
                                                                  cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(result.EnumerateArray());
        Assert.EndsWith("one.txt", result[0].GetProperty("FullName").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_permission_change_restores_access_rules_and_preserves_inheritance(bool folder)
    {
        var path = Path.Combine(_folder, "permissions");
        if (folder)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            File.WriteAllText(path, "test");
        }

        var files = new FileTools(Journal, "test");
        var before = files.ReadCurrent(path);
        var owner = files.AclGet(path);
        Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().Name, owner);
        files.AclSet(path, System.Security.Principal.WindowsIdentity.GetCurrent().Name, "Read", inherit: folder);
        Assert.Contains("Restored", new UndoTools(Journal, [files], "test").UndoLast());
        Assert.Equal(before, files.ReadCurrent(path));
        var ownerSddl = owner.Split('\n')[^1];
        var currentSddl = files.AclGet(path).Split('\n')[^1];
        Assert.Equal(new System.Security.AccessControl.RawSecurityDescriptor(ownerSddl).Owner,
                     new System.Security.AccessControl.RawSecurityDescriptor(currentSddl).Owner);
        Assert.Equal(new System.Security.AccessControl.RawSecurityDescriptor(ownerSddl).ControlFlags.HasFlag(System.Security.AccessControl.ControlFlags.DiscretionaryAclProtected),
                     new System.Security.AccessControl.RawSecurityDescriptor(currentSddl).ControlFlags.HasFlag(System.Security.AccessControl.ControlFlags.DiscretionaryAclProtected));
    }

    [Theory]
    [InlineData("HKLM\\Software\\WindowsAxon")]
    [InlineData("HKCU\\Software\\Policies\\WindowsAxon")]
    [InlineData("HKCU\\software\\microsoft\\windows\\currentversion\\policies\\WindowsAxon")]
    [InlineData("HKCU\\..\\Software")]
    [InlineData("HKCU\\Software\\\\Policies\\WindowsAxon")]
    [InlineData("HKCU\\Software\\.\\Policies\\WindowsAxon")]
    public void Registry_rejects_system_and_policy_writes(string path) => Assert.ThrowsAny<Exception>(() => RegistryTools.ValidatePath(path,
                                                                                                                                       true));

    [Fact]
    public void Real_registry_change_can_be_undone_and_later_changes_require_force()
    {
        var subkey = "Software\\WindowsAxon.Tests\\" + Guid.NewGuid().ToString("N");
        var path = "HKCU\\" + subkey;
        var registry = new RegistryTools(Journal, "test");
        try
        {
            registry.Set(path, "Value", "REG_SZ", JsonSerializer.SerializeToElement("first"));
            registry.Set(path, "Value", "REG_SZ", JsonSerializer.SerializeToElement("second"));
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subkey, true))
            {
                key!.SetValue("Value", "external");
            }

            var undo = new UndoTools(Journal, [registry], "test");
            Assert.Contains("changed after", undo.UndoLast());
            Assert.Contains("Restored", undo.UndoLast(force: true));
            Assert.Contains("first", registry.Read(path, "Value"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subkey, false);
        }
    }

    [Fact]
    public void Deleting_a_binary_registry_value_preserves_its_type_and_bytes_for_undo()
    {
        var subkey = "Software\\WindowsAxon.Tests\\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subkey))
            {
                key.SetValue("Binary", new byte[] { 0, 1, 255 }, Microsoft.Win32.RegistryValueKind.Binary);
            }

            var registry = new RegistryTools(Journal, "test");
            registry.Delete("HKCU/" + subkey.Replace('\\', '/'), "Binary");
            Assert.Contains("Restored", new UndoTools(Journal, [registry], "test").UndoLast());
            using var restored = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subkey)!;
            Assert.Equal(Microsoft.Win32.RegistryValueKind.Binary, restored.GetValueKind("Binary"));
            Assert.Equal(new byte[] { 0, 1, 255 }, (byte[])restored.GetValue("Binary")!);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subkey, false);
        }
    }

    [Fact]
    public void Registry_casing_changes_cannot_undo_a_superseded_value()
    {
        var subkey = "Software\\WindowsAxon.Tests\\" + Guid.NewGuid().ToString("N");
        var path = "HKCU\\" + subkey;
        var registry = new RegistryTools(Journal, "test");
        try
        {
            registry.Set(path, "Value", "REG_SZ", JsonSerializer.SerializeToElement("A"));
            var first = Journal.List().Single();
            registry.Set("HKCU\\" + subkey.ToLowerInvariant(),
                         "value",
                         "REG_SZ",
                         JsonSerializer.SerializeToElement("B"));
            registry.Set(path, "VALUE", "REG_SZ", JsonSerializer.SerializeToElement("A"));
            var undo = new UndoTools(Journal, [registry], "test");

            Assert.False(Journal.IsNewest(first));
            Assert.Contains("superseded", undo.UndoLast(first.Id));
            Assert.Contains("superseded", undo.UndoLast(first.Id, force: true));
            Assert.Contains("A", registry.Read(path, "Value"));
            Assert.Contains("Restored", undo.UndoLast());
            Assert.Contains("B", registry.Read(path, "Value"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subkey, false);
        }
    }

    [Theory]
    [InlineData("browser_snapshot", false)]
    [InlineData("browser_snapshot", true)]
    [InlineData("browser_find", false)]
    [InlineData("browser_find", true)]
    [InlineData("ui_snapshot", false)]
    [InlineData("ui_snapshot", true)]
    [InlineData("ui_screenshot", false)]
    [InlineData("ui_screenshot", true)]
    public void Snapshots_use_the_sensitive_read_approval_gate(string name, bool remote)
    {
        var catalog = new ToolCatalog();
        using var tools = new WindowsToolSet(catalog,
                                             Substitute.For<IChatClient>(),
                                             _folder,
                                             "test",
                                             _ => throw new ArgumentException("No window"),
                                             () =>
                                             {
                                             },
                                             _ =>
                                             {
                                             });
        var policy = new ApprovalPolicy(catalog,
                                        [.. tools.ApprovalRules, new SensitiveReadRule(remote,
                                                                                       "api.example.com")]);

        Assert.IsType<ApprovalRequiredAIFunction>(catalog.BuildAgentTools(entry => entry.Function)
            .Single(tool => tool.Name == name));
        var call = new FunctionCallContent("test", name);
        var decision = policy.Decide(call);
        Assert.Equal(remote ? ApprovalOutcome.Ask : ApprovalOutcome.AutoApprove, decision.Outcome);
        Assert.Equal(remote, decision.AllowForConversation);
        if (remote)
        {
            Assert.Contains("api.example.com", decision.Note);
            policy.AllowForConversation(name);
            Assert.Equal(ApprovalOutcome.AutoApprove, policy.Decide(call).Outcome);
        }
    }

    [Fact]
    public void Screenshot_is_scaled_to_the_maximum_side()
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(2560,
                                                                      1440,
                                                                      96,
                                                                      96,
                                                                      System.Windows.Media.PixelFormats.Bgra32,
                                                                      null,
                                                                      new byte[2560 * 1440 * 4],
                                                                      2560 * 4);

        var image = ScreenshotTools.Encode(source);

        Assert.Equal((1280, 720), (image.Width, image.Height));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], image.Png[..4]);
    }

    [Fact]
    public void Every_paint_action_requires_approval_each_time()
    {
        var catalog = new ToolCatalog();
        using var tools = new WindowsToolSet(catalog,
                                             Substitute.For<IChatClient>(),
                                             _folder,
                                             "test",
                                             _ => throw new ArgumentException("No window"),
                                             () =>
                                             {
                                             },
                                             _ =>
                                             {
                                             });
        var policy = new ApprovalPolicy(catalog, tools.ApprovalRules);
        var names = new[] { "paint_new_canvas", "paint_draw_shape", "paint_draw_free", "paint_add_text", "paint_save" };
        foreach (var name in names)
        {
            Assert.IsType<ApprovalRequiredAIFunction>(catalog.BuildAgentTools(entry => entry.Function)
                .Single(tool => tool.Name == name));
            var call = new FunctionCallContent("test", name);
            var decision = policy.Decide(call);
            Assert.Equal(ApprovalOutcome.Ask, decision.Outcome);
            Assert.False(decision.AllowForConversation);
            policy.AllowForConversation(name);
            Assert.Equal(ApprovalOutcome.Ask, policy.Decide(call).Outcome);
        }
    }

    [Theory]
    [InlineData("file:///C:/secrets")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.com")]
    public void Browser_rejects_non_web_and_credential_urls(string url) => Assert.Throws<ArgumentException>(() => BrowserTools.WebUrl(url));

    [Theory]
    [InlineData("consent")]
    [InlineData("CredentialUIBroker")]
    [InlineData("WindowsAxon.App")]
    [InlineData("Bitwarden")]
    [InlineData("mstsc")]
    public void Desktop_controls_reject_protected_applications(string name) => Assert.Throws<UnauthorizedAccessException>(() => UiSafety.CheckProcess(name));

    [Fact]
    public void Every_tool_belongs_to_the_main_agent_or_to_a_sub_agent()
    {
        var catalog = new ToolCatalog();
        using var tools = new WindowsToolSet(catalog,
                                             Substitute.For<IChatClient>(),
                                             _folder,
                                             "test",
                                             _ => throw new ArgumentException("No window"),
                                             () =>
                                             {
                                             },
                                             _ =>
                                             {
                                             });

        foreach (var entry in catalog.Entries)
        {
            Assert.True(SubAgentCatalog.MainGroups.Contains(entry.Group) || SubAgentCatalog.All.Any(a => a.Includes(entry)),
                        $"{entry.Name} has no agent");
        }

        foreach (var agent in SubAgentCatalog.All)
        {
            Assert.InRange(catalog.Entries.Count(agent.Includes), 1, 14);
        }

        var paintSave = catalog.Find("paint_save")!;
        Assert.Equal(["paint"], SubAgentCatalog.All.Where(a => a.Includes(paintSave)).Select(a => a.Name));
        Assert.True(SubAgentCatalog.Find("desktop")!.Includes(catalog.Find("ui_click")!));
        Assert.Equal(["browser_open", "fs_find_files", "script_create"],
                     catalog.Entries.Where(e => e.Artifact).Select(e => e.Name).Order());
    }

    [Fact]
    public void Catalog_registers_every_planned_group_and_sensitive_actions_remain_guarded()
    {
        var catalog = new ToolCatalog();
        using var tools = new WindowsToolSet(catalog,
                                             Substitute.For<IChatClient>(),
                                             _folder,
                                             "test",
                                             _ => throw new ArgumentException("No window"),
                                             () =>
                                             {
                                             },
                                             _ =>
                                             {
                                             });
        foreach (var group in new[] {
            "system",
            "files",
            "registry",
            "printers",
            "eventlog",
            "personalize",
            "devices",
            "settings",
            "network",
            "apps",
            "utilities",
            "browser",
            "desktop_ui",
            "scripts",
            "docs"
        })
        {
            Assert.Contains(group, catalog.Groups);
        }

        foreach (var name in new[] {
            "fs_delete",
            "acl_set",
            "registry_set_value",
            "process_stop",
            "winget_install",
            "script_run",
            "browser_click",
            "ui_click",
            "fs_read_text",
            "browser_read",
            "ui_read_text"
        })
        {
            Assert.True(catalog.Find(name)!.NeedsApproval, name);
        }

        var policy = new ApprovalPolicy(catalog, tools.ApprovalRules);
        foreach (var name in new[] { "script_run", "browser_click", "ui_click", "undo_last", "clipboard_get" })
        {
            policy.AllowForConversation(name);
            Assert.Equal(ApprovalOutcome.Ask,
                         policy.Decide(new FunctionCallContent("test",
                                                               name,
                                                               new Dictionary<string, object?>())).Outcome);
        }
    }

    private static IChatClient Writer(string code)
    {
        var model = Substitute.For<IChatClient>();
        model.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(),
                               Arg.Any<ChatOptions?>(),
                               Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                                      JsonSerializer.Serialize(new
                                                      {
                                                          Code = code,
                                                          Summary = "Test script",
                                                          Packages = Array.Empty<string>()
                                                      }))));
        return model;
    }

    [Fact]
    public void Management_console_launch_uses_mmc_and_reports_only_verified_windows_as_open()
    {
        var result = SettingsTools.OpenAdmin("diskmgmt.msc", start =>
        {
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "mmc.exe"), start.FileName);
            Assert.True(start.UseShellExecute);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "diskmgmt.msc"), Assert.Single(start.ArgumentList));
            return true;
        });

        Assert.Contains("Opened diskmgmt.msc", result);
        Assert.Contains("visible application window was verified", result);
    }

    [Fact]
    public void Management_console_launch_without_a_window_does_not_report_opened()
    {
        var result = SettingsTools.OpenAdmin("diskmgmt.msc", _ => false);

        Assert.Contains("Launch requested", result);
        Assert.Contains("not verified", result);
        Assert.DoesNotContain("Opened", result);
    }

    [Fact]
    public void Unsupported_management_console_is_rejected_before_launch()
    {
        Assert.Throws<ArgumentException>(() => SettingsTools.OpenAdmin("cmd.exe", _ =>
        {
            Assert.Fail("Unsupported tools must not be launched.");
            return false;
        }));
    }

    [Fact]
    public async Task Successful_script_keeps_child_processes_alive_without_confirming_an_open_window()
    {
        var pidPath = Path.Combine(_folder, "child.pid");
        var code = "Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 120' -WindowStyle Hidden -WorkingDirectory $env:TEMP -PassThru "
            + $"| Select-Object -ExpandProperty Id | Set-Content -LiteralPath '{pidPath.Replace("'", "''")}'\n"
            + "Start-Sleep -Milliseconds 500";
        var scripts = new ScriptTools(Writer(code), _folder, "test");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var draft = JsonSerializer.SerializeToElement(await scripts.Create("powershell", "start child", timeout.Token));

        var result = await scripts.Run(draft.GetProperty("Id").GetString()!, timeout.Token);

        Assert.Contains("Exit code 0", result);
        Assert.Contains("application window was not verified", result);
        Assert.True(File.Exists(pidPath));
        var processId = int.Parse(File.ReadAllText(pidPath).Trim());
        using var child = System.Diagnostics.Process.GetProcessById(processId);
        try
        {
            Assert.False(child.WaitForExit(300), "Successful script terminated its child process.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
            }
        }
    }

    [Fact]
    public async Task Failed_script_still_terminates_child_processes()
    {
        var pidPath = Path.Combine(_folder, "failed-child.pid");
        var code = "Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 120' -WindowStyle Hidden -WorkingDirectory $env:TEMP -PassThru "
            + $"| Select-Object -ExpandProperty Id | Set-Content -LiteralPath '{pidPath.Replace("'", "''")}'\n"
            + "Start-Sleep -Milliseconds 500\nthrow 'expected-failure'";
        var scripts = new ScriptTools(Writer(code), _folder, "test");
        var draft = JsonSerializer.SerializeToElement(await scripts.Create("powershell",
                                                                           "start then fail",
                                                                           TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() => scripts.Run(draft.GetProperty("Id").GetString()!,
                                                                              TestContext.Current.CancellationToken));

        AssertChildExited(pidPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_timed_out_script_still_terminates_child_processes(bool timedOut)
    {
        var pidPath = Path.Combine(_folder, "stopped-child.pid");
        var escapedPidPath = pidPath.Replace("'", "''");
        // Publish the PID only after the file is closed, so cancellation cannot race the test's read.
        var code = "Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 120' -WindowStyle Hidden -WorkingDirectory $env:TEMP -PassThru "
            + $"| Select-Object -ExpandProperty Id | Set-Content -LiteralPath '{escapedPidPath}.tmp'\n"
            + $"Move-Item -LiteralPath '{escapedPidPath}.tmp' -Destination '{escapedPidPath}'\n"
            + "Start-Sleep -Seconds 120";
        var scripts = new ScriptTools(Writer(code), _folder, "test");
        var draft = JsonSerializer.SerializeToElement(await scripts.Create("powershell",
                                                                           "start then wait",
                                                                           TestContext.Current.CancellationToken));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        var running = scripts.Run(draft.GetProperty("Id").GetString()!, stop.Token);
        for (var attempt = 0; attempt < 100 && !File.Exists(pidPath); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.True(File.Exists(pidPath), "Script child did not start.");
        if (timedOut)
        {
            stop.CancelAfter(TimeSpan.FromMilliseconds(100));
        }
        else
        {
            stop.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        AssertChildExited(pidPath);
    }

    private static void AssertChildExited(string pidPath)
    {
        var processId = int.Parse(File.ReadAllText(pidPath).Trim());
        System.Diagnostics.Process child;
        try
        {
            child = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // The process has already exited and been removed from the process table.
            return;
        }

        using (child)
        {
            try
            {
                Assert.True(child.WaitForExit(5000), "Failed or stopped script child remained alive.");
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000);
                }
            }
        }
    }

    [Fact]
    public async Task Scripts_are_parsed_reviewed_executed_and_tampering_is_refused()
    {
        var scripts = new ScriptTools(Writer("Write-Output 'windowsaxon-script-test'"), _folder, "test");
        var draft = JsonSerializer.SerializeToElement(await scripts.Create("powershell",
                                                                           "print test",
                                                                           TestContext.Current.CancellationToken));
        var id = draft.GetProperty("Id").GetString()!;
        Assert.Contains("Write-Output",
                        scripts.Decide(new FunctionCallContent("1",
                                                               "script_run",
                                                               new Dictionary<string, object?> { ["id"] = id })).Note);
        Assert.Contains("windowsaxon-script-test", await scripts.Run(id, TestContext.Current.CancellationToken));
        File.AppendAllText(draft.GetProperty("Path").GetString()!, "\nWrite-Output 'tampered'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scripts.Run(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_script_with_invalid_syntax_is_not_saved_as_runnable()
    {
        var scripts = new ScriptTools(Writer("if ("), _folder, "test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scripts.Create("powershell",
                                                                                 "invalid",
                                                                                 TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Script_output_includes_standard_error_and_failures_are_saved()
    {
        var scripts = new ScriptTools(Writer("[Console]::Error.WriteLine('diagnostic'); Write-Output 'ok'"),
                                      _folder,
                                      "test");
        var draft = JsonSerializer.SerializeToElement(await scripts.Create("powershell",
                                                                           "output",
                                                                           TestContext.Current.CancellationToken));
        Assert.Contains("diagnostic",
                        await scripts.Run(draft.GetProperty("Id").GetString()!,
                                          TestContext.Current.CancellationToken));
        var failing = new ScriptTools(Writer("throw 'expected-script-error'"), _folder, "test");
        var bad = JsonSerializer.SerializeToElement(await failing.Create("powershell",
                                                                         "failure",
                                                                         TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Run(bad.GetProperty("Id").GetString()!,
                                                                              TestContext.Current.CancellationToken));
        Assert.Contains("expected-script-error",
                        File.ReadAllText(Path.ChangeExtension(bad.GetProperty("Path").GetString()!,
                                                              ".out.txt")));
    }

    [Fact]
    public async Task PowerShell_script_output_preserves_Chinese_text()
    {
        var scripts = new ScriptTools(Writer("Write-Output '\u53f0\u7063'"), _folder, "test");
        var draft = JsonSerializer.SerializeToElement(await scripts.Create("powershell",
                                                                           "text",
                                                                           TestContext.Current.CancellationToken));
        Assert.Contains("\u53f0\u7063",
                        await scripts.Run(draft.GetProperty("Id").GetString()!,
                                          TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recycling_refuses_network_paths()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => new FileTools(Journal, "test").Delete(@"\\localhost\C$\windowsaxon-nonexistent-test.txt"));
    }

    [Fact]
    public async Task Stopping_a_script_terminates_its_process()
    {
        var pidPath = Path.Combine(_folder, "script.pid");
        var escapedPidPath = pidPath.Replace("'", "''");
        // Publish the PID only after the file is closed, so cancellation cannot race the test's read.
        var code = $"Set-Content -LiteralPath '{escapedPidPath}.tmp' -Value $PID\n"
            + $"Move-Item -LiteralPath '{escapedPidPath}.tmp' -Destination '{escapedPidPath}'\n"
            + "Start-Sleep -Seconds 120";
        var scripts = new ScriptTools(Writer(code),
                                      _folder,
                                      "test");
        var draft = JsonSerializer.SerializeToElement(await scripts.Create("powershell",
                                                                           "wait",
                                                                           TestContext.Current.CancellationToken));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = scripts.Run(draft.GetProperty("Id").GetString()!, stop.Token);
        for (var attempt = 0; attempt < 100 && !File.Exists(pidPath); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(pidPath), "Script did not start.");
        var processId = int.Parse(File.ReadAllText(pidPath).Trim());
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(process.WaitForExit(5000), "Stopped script process remained alive.");
    }

    public async ValueTask DisposeAsync()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 49)
            {
                // Windows may release a terminated process's working directory after termination is reported.
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }
}
