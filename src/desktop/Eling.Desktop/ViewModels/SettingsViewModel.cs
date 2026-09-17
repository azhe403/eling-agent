using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Eling.Desktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Eling.Desktop.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly ElingApiClient _apiClient;
    private readonly DesktopSettingsStore _settings;
    private readonly ILogger<SettingsViewModel> _logger;

    private string _backendUrl = "";
    private string _providerBaseUrl = "";
    private string _apiKey = "";
    private string _selectedModel = "";
    private string _statusText = "";
    private bool _isBusy;

    public string BackendUrl { get => _backendUrl; set => this.RaiseAndSetIfChanged(ref _backendUrl, value); }
    public string ProviderBaseUrl { get => _providerBaseUrl; set => this.RaiseAndSetIfChanged(ref _providerBaseUrl, value); }
    public string ApiKey { get => _apiKey; set => this.RaiseAndSetIfChanged(ref _apiKey, value); }
    public string SelectedModel { get => _selectedModel; set => this.RaiseAndSetIfChanged(ref _selectedModel, value); }
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    public ObservableCollection<string> Models { get; } = [];

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> FetchModelsCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> TestCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SaveCommand { get; }

    public SettingsViewModel(
        ElingApiClient apiClient,
        DesktopSettingsStore settings,
        ILogger<SettingsViewModel> logger)
    {
        _apiClient = apiClient;
        _settings = settings;
        _logger = logger;

        LoadCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
        FetchModelsCommand = ReactiveCommand.CreateFromTask(() => FetchModelsAsync());
        TestCommand = ReactiveCommand.CreateFromTask(() => TestAsync());
        SaveCommand = ReactiveCommand.CreateFromTask(() => SaveAsync());
    }

    public async Task LoadAsync()
    {
        try
        {
            BackendUrl = _settings.GetBackendUrl() ?? "";
            var provider = await _apiClient.GetProviderAsync();
            if (provider != null)
            {
                ProviderBaseUrl = provider.BaseUrl ?? "";
                SelectedModel = provider.Model ?? "";
                Models.Clear();
                foreach (var model in provider.ModelsCached)
                {
                    Models.Add(model);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    private async Task FetchModelsAsync()
    {
        _logger.LogInformation("Settings fetch models");
        IsBusy = true;
        try
        {
            var models = await _apiClient.FetchModelsAsync();
            Models.Clear();
            foreach (var model in models)
            {
                Models.Add(model);
            }

            StatusText = models.Count == 0 ? "No models returned." : $"Found {models.Count} model(s).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch models");
            StatusText = $"Fetch failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestAsync()
    {
        _logger.LogInformation("Settings test provider");
        IsBusy = true;
        try
        {
            var result = await _apiClient.TestProviderAsync();
            StatusText = result.Ok ? $"OK: {result.Message}" : $"Failed: {result.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test provider");
            StatusText = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        _logger.LogInformation("Settings save backendUrlSet={BackendSet} providerSet={ProviderSet} keyProvided={KeyProvided}",
            !string.IsNullOrWhiteSpace(BackendUrl), !string.IsNullOrWhiteSpace(ProviderBaseUrl), !string.IsNullOrEmpty(ApiKey));
        IsBusy = true;
        try
        {
            _settings.SetBackendUrl(string.IsNullOrWhiteSpace(BackendUrl) ? null : BackendUrl.Trim());
            var saved = await _apiClient.UpdateProviderAsync(
                string.IsNullOrWhiteSpace(ProviderBaseUrl) ? null : ProviderBaseUrl.Trim(),
                string.IsNullOrWhiteSpace(SelectedModel) ? null : SelectedModel.Trim(),
                string.IsNullOrEmpty(ApiKey) ? null : ApiKey);
            StatusText = saved != null ? "Saved." : "Save failed: backend unreachable.";
            ApiKey = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            StatusText = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
