using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DotaTune.Core.Config;
using DotaTune.Core.Platform;
using DotaTune.Resources;
using DotaTune.ViewModels;
using DotaTune.Views;

namespace DotaTune.Tests;

public class ConfigPageTests
{
    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Choice_is_saved_written_and_removed(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, language);
        string autoexec = AutoexecService.PathIn(steam.GameRoot);

        Assert.Equal(Strings.ConfigNothingWritten, page.Summary);
        Assert.False(page.CanWriteNow);
        Assert.False(page.CanRemoveNow);

        ColorRow friendly = page.Colors[0];
        friendly.IsOn = true;
        friendly.SelectedSwatch = friendly.Swatches.Single(s => s.Color == new Rgb(255, 140, 0));
        page.CreepScale = page.CreepScales.Single(o => o.Value == 1.5);
        page.HideFrontpageVideo = true;
        Assert.Equal(new Rgb(255, 140, 0), App.Services.Settings.Load().Config!.FriendlyColor);
        Assert.Equal("#FF8C00", friendly.Hex);
        Assert.Equal(Strings.ConfigNotWrittenYet, page.Summary);

        page.WriteCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.ConfigWritten, page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.CanWriteNow);
        Assert.Equal(
            ["dota_friendly_color 255 140 0", "dota_minimap_creep_scale 1.5", "dota_hide_frontpage_video 1"],
            AutoexecText.BlockLines(File.ReadAllText(autoexec))!.Select(l => l.ToString()));
        Snapshot(window, $"{language}-Config-written.png");

        // A typed code that is not in the palette leaves no swatch chosen.
        friendly.Hex = "#123456";
        Assert.Null(friendly.SelectedSwatch);
        Assert.Equal(Strings.ConfigDiffers, page.Summary);
        Assert.True(page.IsAttention);
        Assert.True(page.CanWriteNow);

        page.RemoveCommand.Execute(null);
        Settle(page);
        Assert.False(File.Exists(autoexec));
        Assert.Equal(Strings.ConfigNotWrittenYet, page.Summary);
        window.Close();
    }

    [AvaloniaFact]
    public void Minimalism_switch_writes_the_preset_and_leaves_the_colours_as_they_are()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, "ru");

        page.Minimalism = true;
        Assert.True(App.Services.Settings.Load().Config!.Minimalism);
        Assert.All(page.Colors, c => Assert.False(c.IsOn));
        Assert.False(page.NoParticleLights);
        Assert.Equal(Strings.ConfigNotWrittenYet, page.Summary);

        page.WriteCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.ConfigWritten, page.Summary);
        Assert.Equal(MinimalismPreset.Lines, AutoexecText.BlockLines(File.ReadAllText(AutoexecService.PathIn(steam.GameRoot))));
        Snapshot(window, "ru-Config-minimalism.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Saved_choice_comes_back_and_running_dota_blocks_the_buttons()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.Running.Add("dota2");
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        App.Services.Settings.Save(new Core.Settings.AppSettings
        {
            Config = new GameConfig { EnemyColor = new Rgb(1, 2, 3), CreepScale = 1.7, MenuFpsLimit = 144 },
        });
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, "ru", keepServices: true);

        Assert.True(page.Colors[1].IsOn);
        Assert.Equal("#010203", page.Colors[1].Hex);
        Assert.Null(page.Colors[1].SelectedSwatch);
        Assert.False(page.Colors[0].IsOn);
        Assert.Equal(1.7, page.CreepScale.Value);
        Assert.Equal(144, page.MenuFps.Value);
        Assert.Equal(Strings.ConfigDotaRunning, page.Note);
        Assert.False(page.CanWriteNow);
        window.Close();
    }

    [AvaloniaFact]
    public void Uninstall_takes_the_lines_out()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, "ru");
        page.NoParticleLights = true;
        page.WriteCommand.Execute(null);
        Settle(page);
        Assert.True(File.Exists(AutoexecService.PathIn(steam.GameRoot)));
        window.Close();

        App.Services.RemoveFromGame();

        Assert.False(File.Exists(AutoexecService.PathIn(steam.GameRoot)));
        Assert.Null(App.Services.Autoexec.Current);
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Launch_options_are_written_and_put_back(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-prewarm -high -nojoy");
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, language);
        LaunchBlockViewModel launch = page.Launch;

        Assert.True(launch.IsAvailable);
        Assert.False(launch.NoVideo);
        Assert.True(launch.NoJoystick);
        Assert.Equal("-prewarm", launch.DeadOptions);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.LaunchResultFormat, "-high -nojoy"), launch.Result);
        Assert.True(launch.CanWriteNow);

        launch.NoVideo = true;
        Assert.Equal("-high -nojoy -novid", launch.Planned);
        launch.WriteCommand.Execute(null);
        Settle(page);
        Assert.Equal("-high -nojoy -novid", Core.Steam.LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Equal(Strings.LaunchWritten, launch.Note);
        Assert.Null(launch.Result);
        Assert.Null(launch.DeadOptions);
        Assert.False(launch.CanWriteNow);
        Assert.True(launch.CanRestoreNow);
        Snapshot(window, $"{language}-Config-launch.png", scrollToEnd: true);

        launch.RestoreCommand.Execute(null);
        Settle(page);
        Assert.Equal("-prewarm -high -nojoy", Core.Steam.LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Equal(Strings.LaunchRestored, launch.Note);
        Assert.False(launch.CanRestoreNow);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Running_steam_is_restarted_for_the_launch_options_and_uninstall_puts_the_line_back(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-prewarm");
        steam.Running.Add("steam");
        var client = new FakeSteamClient(steam);
        App.Services = AppServices.Create(
            new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read, steamRestart: log => SteamRestartTests.Restart(steam, client, log));
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, language, keepServices: true);
        LaunchBlockViewModel launch = page.Launch;

        Assert.Null(launch.Note);
        Assert.NotNull(launch.Result);
        Assert.True(launch.CanWriteNow);
        Assert.Equal(Strings.LaunchRestartAndWrite, launch.WriteText);
        Snapshot(window, $"{language}-Config-launch-steam.png", scrollToEnd: true);

        launch.WriteCommand.Execute(null);
        Settle(page);
        Assert.Equal(string.Empty, Core.Steam.LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Equal($"{Strings.LaunchWritten} {Strings.LaunchSteamStarting}", launch.Note);
        Assert.Equal(["-shutdown", ""], client.Starts.Select(s => string.Join(' ', s.Skip(1))));
        Assert.Contains("steam", steam.Running);
        Assert.True(launch.CanRestoreNow);
        Assert.Equal(Strings.LaunchRestartAndRestore, launch.RestoreText);

        // Closing Steam would end the game, so a running Dota hides the buttons.
        steam.Running.Add("dota2");
        page.Activated();
        Settle(page);
        Assert.Equal(Strings.LaunchDotaRunning, launch.Note);
        Assert.False(launch.CanRestoreNow);
        launch.NoVideo = true;
        Assert.NotNull(launch.Result);
        Assert.False(launch.CanWriteNow);
        steam.Running.Remove("dota2");
        window.Close();

        steam.Running.Clear();
        App.Services.RemoveFromGame();

        Assert.Equal("-prewarm", Core.Steam.LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Null(App.Services.Launch.ChangeOf(FakeSteam.AccountId));
    }

    [AvaloniaFact]
    public void Restart_writes_with_the_current_login_format_and_a_steam_that_shows_no_account()
    {
        // As on 27.10.2026: loginusers.vdf without MostRecent, and the started Steam leaves ActiveUser at 0.
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.UseCurrentLoginFormat();
        steam.SetLaunchOptions("-prewarm");
        steam.Running.Add("steam");
        var client = new FakeSteamClient(steam) { ActiveUserAfterStart = 0 };
        App.Services = AppServices.Create(
            new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read, steamRestart: log => SteamRestartTests.Restart(steam, client, log));
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, "ru", keepServices: true);
        LaunchBlockViewModel launch = page.Launch;

        launch.WriteCommand.Execute(null);
        Settle(page);

        Assert.Equal(string.Empty, Core.Steam.LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Equal($"{Strings.LaunchWritten} {Strings.LaunchSteamStarting}", launch.Note);
        Assert.Equal(0u, steam.ActiveUser);
        Assert.Null(steam.Read().Problem);
        Assert.Equal(FakeSteam.AccountId, steam.Read().Steam!.Account!.AccountId);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Settings_are_transferred_and_put_back(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.WriteGameFile(FakeSteam.OtherAccountId, "remote/cfg/dotakeys_personal.lst", "keys from");
        string keys = steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/dotakeys_personal.lst", "keys into");
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, language, section: SectionKind.Settings);
        TransferBlockViewModel transfer = page.Transfer;

        Assert.True(transfer.IsAvailable);
        Assert.Equal(FakeSteam.OtherAccountId, transfer.Source!.Account.AccountId);
        Assert.Equal(FakeSteam.AccountId, transfer.Target!.Account.AccountId);
        Assert.Equal("demo_player (222222222)", transfer.Target.Text);
        Assert.False(transfer.Groups.Single(g => g.Group == TransferGroup.LaunchOptions).IsChecked);
        Assert.True(transfer.CanTransferNow);
        Assert.False(transfer.CanRestoreNow);

        transfer.Source = transfer.Target;
        Assert.Equal(Strings.TransferSameAccount, transfer.Note);
        Assert.False(transfer.CanTransferNow);
        transfer.Source = transfer.Accounts[0];
        Assert.Null(transfer.Note);

        transfer.TransferCommand.Execute(null);
        Settle(page);
        Assert.Equal("keys from", File.ReadAllText(keys));
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.TransferDoneFormat, 1), transfer.Note);
        Assert.NotNull(transfer.CloudHint);
        Assert.True(transfer.CanRestoreNow);
        // Done: the button does not stay as if the transfer were still to do.
        Assert.False(transfer.CanTransferNow);
        Snapshot(window, $"{language}-Settings-transfer.png", scrollToEnd: true);

        transfer.RestoreCommand.Execute(null);
        Settle(page);
        Assert.Equal("keys into", File.ReadAllText(keys));
        Assert.Equal(Strings.TransferRestored, transfer.Note);
        Assert.Null(transfer.CloudHint);
        Assert.False(transfer.CanRestoreNow);
        Assert.True(transfer.CanTransferNow);

        transfer.TransferCommand.Execute(null);
        Settle(page);
        Assert.False(transfer.CanTransferNow);

        // The message of the last run does not outlive a new choice, and the button is back for it.
        transfer.Groups[0].IsChecked = false;
        Assert.Null(transfer.Note);
        Assert.True(transfer.CanTransferNow);
        window.Close();
    }

    [AvaloniaFact]
    public void Single_account_has_nothing_to_transfer_and_launch_tick_waits_for_steam()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/chat.cfg", "chat");
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, "ru", section: SectionKind.Settings);

        Assert.False(page.Transfer.IsAvailable);
        Assert.Equal(Strings.TransferOneAccount, page.Transfer.Note);
        Assert.False(page.Transfer.CanTransferNow);
        window.Close();

        steam.WriteGameFile(FakeSteam.OtherAccountId, "remote/cfg/chat.cfg", "chat");
        steam.Running.Add("steam");
        (window, page) = Open(folder, steam, "ru", section: SectionKind.Settings);
        Assert.True(page.Transfer.CanTransferNow);
        page.Transfer.Groups.Single(g => g.Group == TransferGroup.LaunchOptions).IsChecked = true;
        Assert.Equal(Strings.TransferSteamRunning, page.Transfer.Note);
        Assert.False(page.Transfer.CanTransferNow);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Preset_code_shows_what_changes_and_puts_the_choice_on_the_page(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-high");
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, language, section: SectionKind.Settings);
        PresetBlockViewModel presets = page.Presets;
        string code = Core.Presets.PresetCode.Encode(new Core.Presets.PresetChoice
        {
            Effects = ["Weather", "RainOfFrogs"],
            Config = new GameConfig { EnemyColor = new Rgb(255, 140, 0), CreepScale = 1.7 },
            Launch = ["-novid", "-autoconfig"],
        });

        presets.ImportText = "  " + code + "\n";
        presets.LoadCodeCommand.Execute(null);
        Assert.True(presets.HasPending);
        Assert.False(presets.HasHotkeys);
        Assert.Contains($"{Strings.ConfigCreepScale} {string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ConfigTimesFormat, 1.75)}", presets.ConfigChange, StringComparison.Ordinal);
        Assert.DoesNotContain("dota_", presets.ConfigChange, StringComparison.Ordinal);
        Assert.Contains("-novid", presets.LaunchChange, StringComparison.Ordinal);
        Assert.Contains("RainOfFrogs", presets.Unknown!, StringComparison.Ordinal);
        Assert.Contains("-autoconfig", presets.Unknown!, StringComparison.Ordinal);
        Snapshot(window, $"{language}-Settings-preset.png", scrollToEnd: true);

        // A code carries no files: with every line switched off there is nothing to apply.
        presets.ApplyEffects = presets.ApplyConfig = presets.ApplyLaunch = false;
        Assert.False(presets.ApplyCommand.CanExecute(null));
        presets.ApplyEffects = presets.ApplyConfig = true;
        presets.ApplyCommand.Execute(null);
        Settle(page);
        Assert.False(presets.HasPending);
        Assert.Equal([Core.Effects.EffectGroup.Weather], App.Services.Settings.Load().EffectGroups);
        Assert.True(page.Colors[1].IsOn);
        Assert.Equal("#FF8C00", page.Colors[1].Hex);
        Assert.Equal(1.75, page.CreepScale.Value);
        Assert.Equal(new Rgb(255, 140, 0), App.Services.Settings.Load().Config!.EnemyColor);
        Assert.False(page.Launch.NoVideo);
        Assert.Equal("-high", page.Launch.Planned);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.PresetAppliedFormat, 0), presets.Note);

        presets.ImportText = "DT9-AAAA";
        presets.LoadCodeCommand.Execute(null);
        Assert.Equal(Strings.PresetNewer, presets.Note);
        Assert.False(presets.HasPending);
        window.Close();
    }

    [AvaloniaFact]
    public void Preset_file_carries_hotkeys_into_the_account_and_puts_them_back()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        string keys = steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/dotakeys_personal.lst", "\"KeyBindings\"\n{\n\t\"Name\"\t\t\"мои\"\n}\n");
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, "ru", section: SectionKind.Settings);
        PresetBlockViewModel presets = page.Presets;
        string file = Path.Combine(folder.Path, "набор.dotatune");

        presets.WithHotkeys = true;
        Wait(presets.SaveToAsync(file));
        Settle(page);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.PresetSavedFormat, "набор.dotatune"), presets.Note);
        File.WriteAllText(keys, "\"KeyBindings\"\n{\n\t\"Name\"\t\t\"другие\"\n}\n");

        Wait(presets.OpenFromAsync(file));
        Assert.True(presets.HasHotkeys);
        presets.ApplyEffects = presets.ApplyConfig = presets.ApplyLaunch = false;
        presets.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Contains("\"мои\"", File.ReadAllText(keys), StringComparison.Ordinal);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.PresetAppliedFormat, 1), presets.Note);
        Assert.True(page.Transfer.CanRestoreNow);

        page.Transfer.RestoreCommand.Execute(null);
        Settle(page);
        Assert.Contains("\"другие\"", File.ReadAllText(keys), StringComparison.Ordinal);
        Assert.False(page.Transfer.CanRestoreNow);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Settings_copy_is_saved_restored_part_by_part_and_undone(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-novid");
        string keys = steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/dotakeys_personal.lst", "keys mine");
        string video = steam.WriteGameFile(FakeSteam.AccountId, "local/cfg/video.txt", "video mine");
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, language, section: SectionKind.Settings);
        BackupBlockViewModel backup = page.Backup;
        string file = Path.Combine(folder.Path, "копия.zip");
        Assert.False(backup.HasLastCopy);

        Wait(backup.SaveToAsync(file));
        Settle(page);
        string inside = string.Join(", ", new[] { Strings.TransferGraphics, Strings.TransferHotkeys, Strings.TransferLaunchOptions }
            .Select(t => t.ToLower(System.Globalization.CultureInfo.CurrentCulture)));
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.BackupSavedFormat, "копия.zip", inside), backup.Note);
        Assert.True(backup.HasLastCopy);
        Assert.Contains("копия.zip", backup.LastCopyText, StringComparison.Ordinal);
        File.WriteAllText(keys, "keys changed");
        File.WriteAllText(video, "video changed");

        Wait(backup.RestoreLastCommand.ExecuteAsync(null));
        Assert.True(backup.HasPending);
        Assert.Equal([Core.Presets.BackupPart.Graphics, Core.Presets.BackupPart.Hotkeys, Core.Presets.BackupPart.LaunchOptions], backup.Rows.Select(r => r.Part));
        Assert.All(backup.Rows, r => Assert.True(r.IsChecked));
        Assert.Equal("-novid", backup.Rows[2].Description);
        Assert.Equal(Strings.BackupApply, backup.ApplyText);
        backup.Rows[0].IsChecked = false;
        Snapshot(window, $"{language}-Settings-backup.png");

        backup.ApplyCommand.Execute(null);
        Settle(page);
        Assert.False(backup.HasPending);
        Assert.Equal("keys mine", File.ReadAllText(keys));
        Assert.Equal("video changed", File.ReadAllText(video));
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.BackupRestoredFormat, 1), backup.Note);
        Assert.True(backup.CanUndo);

        backup.UndoCommand.Execute(null);
        Settle(page);
        Assert.Equal("keys changed", File.ReadAllText(keys));
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.BackupUndoneFormat, 1), backup.Note);
        Assert.False(backup.CanUndo);

        File.WriteAllText(file, "not a zip");
        Wait(backup.OpenFromAsync(file));
        Assert.Equal(Strings.BackupNotACopy, backup.Note);
        Assert.False(backup.HasPending);
        window.Close();
    }

    [AvaloniaFact]
    public void Copy_from_another_computer_leaves_graphics_off_and_restarts_steam_for_the_launch_line()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-high");
        string video = steam.WriteGameFile(FakeSteam.AccountId, "local/cfg/video.txt", "video here");
        steam.Running.Add("steam");
        var client = new FakeSteamClient(steam);
        App.Services = AppServices.Create(
            new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read, steamRestart: log => SteamRestartTests.Restart(steam, client, log));
        (MainWindow window, ConfigViewModel page) = Open(folder, steam, "ru", keepServices: true, section: SectionKind.Settings);
        BackupBlockViewModel backup = page.Backup;
        string file = Path.Combine(folder.Path, "с другого.zip");
        var manifest = new Core.Presets.BackupManifest(
            Core.Presets.SettingsBackupFile.FormatVersion, Core.Presets.SettingsBackupFile.Kind, "0.1.0", null, DateTimeOffset.Now, "другой", "0000", "-novid");
        Core.Presets.SettingsBackupFile.Write(file, new Core.Presets.SettingsBackup(
            manifest, new Dictionary<string, byte[]> { ["local/cfg/video.txt"] = System.Text.Encoding.UTF8.GetBytes("video there") }, null));

        Wait(backup.OpenFromAsync(file));
        Assert.False(backup.Rows[0].IsChecked);
        Assert.Equal(Strings.BackupGraphicsOther, backup.Rows[0].Description);
        Assert.Equal(Strings.BackupApplyRestart, backup.ApplyText);
        backup.Rows[1].IsChecked = false;
        Assert.False(backup.ApplyCommand.CanExecute(null));
        backup.Rows[1].IsChecked = true;

        backup.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal("-novid", Core.Steam.LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Equal("video here", File.ReadAllText(video));
        Assert.Equal($"{string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.BackupRestoredFormat, 1)} {Strings.LaunchSteamStarting}", backup.Note);
        Assert.Equal(["-shutdown", ""], client.Starts.Select(s => string.Join(' ', s.Skip(1))));
        window.Close();
    }

    [AvaloniaFact]
    public void Copy_transfer_and_presets_are_on_the_settings_page_and_the_game_blocks_stay()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, _) = Open(folder, steam, "ru");
        // Taken after Open: it sets the language the strings come in.
        string[] moved = [Strings.ConfigBackupGroup, Strings.ConfigTransferGroup, Strings.ConfigPresetGroup];
        string[] kept = [Strings.ConfigCommandsGroup, Strings.ConfigLaunchGroup];
        HashSet<string> shown = ShownTexts(window);
        Assert.All(kept, t => Assert.Contains(t, shown));
        Assert.All(moved, t => Assert.DoesNotContain(t, shown));
        window.Close();

        (window, ConfigViewModel page) = Open(folder, steam, "ru", section: SectionKind.Settings);
        shown = ShownTexts(window);
        Assert.All(moved, t => Assert.Contains(t, shown));
        Assert.All(kept, t => Assert.DoesNotContain(t, shown));
        Assert.Contains(Strings.LanguageLabel, shown);
        // Without them the buttons of the blocks do nothing at all.
        Assert.NotNull(page.Backup.PickSavePath);
        Assert.NotNull(page.Backup.PickOpenPath);
        Assert.NotNull(page.Presets.PickSavePath);
        Assert.NotNull(page.Presets.PickOpenPath);
        Assert.NotNull(page.Presets.CopyText);

        // A new language rebuilds the window, so it waits while the blocks write.
        Avalonia.Controls.ComboBox language = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Avalonia.Controls.ComboBox>().Single(c => c.ItemsSource is IReadOnlyList<LanguageOption>);
        Assert.True(language.IsEnabled);
        page.IsBusy = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(language.IsEnabled);
        page.IsBusy = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(language.IsEnabled);
        window.Close();
    }

    private static HashSet<string> ShownTexts(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        return [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text is not null).Select(t => t.Text!)];
    }

    /// <summary>Opens the window on <paramref name="section"/>; the model is the config page's, which also runs the blocks of the settings page.</summary>
    private static (MainWindow, ConfigViewModel) Open(
        CyrillicTempFolder folder, FakeSteam steam, string language, bool keepServices = false, SectionKind section = SectionKind.Config)
    {
        if (!keepServices)
        {
            App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        }

        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(section);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (ConfigViewModel)main.Sections.Single(s => s.Kind == SectionKind.Config).Page;
        Settle(page);
        return (window, page);
    }

    private static void Snapshot(MainWindow window, string name, bool scrollToEnd = false)
    {
        for (int i = 0; i < 4; i++)
        {
            // After the first pass the page has its full height, so the end is where it stays.
            if (scrollToEnd && i == 1)
            {
                foreach (Avalonia.Controls.ScrollViewer scroll in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ScrollViewer>())
                {
                    scroll.ScrollToEnd();
                }
            }

            Thread.Sleep(100);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        string dir = Path.Combine(RepoPaths.Root, "artifacts", "snapshots");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>Runs the UI queue until the task ends; blocking on it would stall the UI thread it continues on.</summary>
    private static void Wait(Task task)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
    }

    private static void Settle(ConfigViewModel page)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (page.IsBusy && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(page.IsBusy, "the config page did not settle in 10 s");
    }
}
