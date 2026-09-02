using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Utilities;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.Helpers;

namespace SPTarkov.Launcher;

/// <summary>
/// Mostly asynchronous base class for MudBlazor setting components.
/// It implements support for debounced path validation to prevent operations from
/// blocking the UI thread and to reduce IO calls.
/// </summary>
public abstract class SettingComponentBase : ComponentBase
{
    [Inject]
    protected ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    protected LocaleHelper LocaleHelper { get; set; } = default!;

    [Inject]
    protected ConfigHelper ConfigHelper { get; set; } = default!;

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public bool AddDiv { get; set; }

    protected string NewValue { get; set; } = "";
    protected bool HasError { get; set; }
    private CancellationTokenSource? _debounceCts;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
    }

    protected string GetLinkClasses()
    {
        var classes = new CssBuilder()
            .AddClass("d-flex")
            .AddClass("justify-center")
            .AddClass("align-center")
            .AddClass("py-2")
            .AddClass("pl-5");

        if (!Disabled)
        {
            classes.AddClass("cursor-pointer").AddClass("setting-on-hover");
        }

        return classes.Build();
    }

    protected async Task OnNewValueChanged(string value)
    {
        NewValue = value;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            await Task.Delay(300, token);

            HasError = await Task.Run(() => ValidateValue(NewValue), token);

            StateHasChanged();
        }
        catch (TaskCanceledException) { }
    }

    protected async Task SetFilePath()
    {
        var file = await Launcher.App.MainWindow.ShowOpenFileAsync(
            title: "Choose File",
            defaultPath: FilePickerHelper.StartDirectory(NewValue)
        );

        // no file was selected
        if (!file.Any())
        {
            return;
        }

        NewValue = file.FirstOrDefault()!;
        HasError = ValidateValue(NewValue);

        await Save();
    }

    protected async Task SetFolderPath()
    {
        var folder = await Launcher.App.MainWindow.ShowOpenFolderAsync(
            title: "Choose Folder",
            defaultPath: FilePickerHelper.StartDirectory(NewValue)
        );

        // no folder was selected
        if (!folder.Any())
        {
            return;
        }

        NewValue = folder.FirstOrDefault()!;
        HasError = ValidateValue(NewValue);

        await Save();
    }

    protected async Task Save()
    {
        if (HasError)
        {
            Snackbar.Add(LocaleHelper.Get("setting_save_path_error_1"), Severity.Error);
            return;
        }

        await SaveValue(NewValue);
        StateHasChanged();
    }

    protected virtual bool ValidateValue(string value)
    {
        return false;
    }

    protected virtual Task SaveValue(string value)
    {
        return Task.CompletedTask;
    }
}
