using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using MobileData.Core;
using MobileData.Providers;

namespace MobileData.App;

[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid("63025828-B62F-4FE9-AAF5-A83E7162E090")] // CRITICAL: Must match Program.cs and manifest
public sealed class WidgetProvider : IWidgetProvider, IWidgetProvider2
{
    private const string RogersWidgetId = "Rogers_Data_Widget";
    private const string FidoWidgetId = "Fido_Data_Widget";

    private readonly Dictionary<string, WidgetState> _activeWidgets = new();
    private readonly Dictionary<string, IDataProvider> _providers = new();
    private Timer? _refreshTimer;
    private readonly object _lock = new();

    private sealed class WidgetState
    {
        public string DefinitionId { get; set; } = "";
        public WidgetSize Size { get; set; }
    }

    public WidgetProvider()
    {
        System.Diagnostics.Debug.WriteLine("WidgetProvider constructor called");

        var rogers = new RogersProvider();
        rogers.DataChanged += OnProviderDataChanged;
        _providers[RogersWidgetId] = rogers;

        var fido = new FidoProvider();
        fido.DataChanged += OnProviderDataChanged;
        _providers[FidoWidgetId] = fido;
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        System.Diagnostics.Debug.WriteLine($"CreateWidget called for: {widgetContext.Id}");
        _activeWidgets[widgetContext.Id] = new WidgetState
        {
            DefinitionId = widgetContext.DefinitionId,
            Size = widgetContext.Size
        };

        EnsureTimerRunning();
        UpdateWidget(widgetContext.Id);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        _activeWidgets.Remove(widgetId);
        StopTimerIfNoWidgets();
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
    }

    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs customizationRequestedArgs)
    {
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        if (_activeWidgets.TryGetValue(contextChangedArgs.WidgetContext.Id, out var state))
        {
            state.Size = contextChangedArgs.WidgetContext.Size;
        }
        UpdateWidget(contextChangedArgs.WidgetContext.Id);
    }

    public void Activate(WidgetContext widgetContext)
    {
        try
        {
            CreateWidget(widgetContext);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Activate failed: {ex.Message}");
        }
    }

    public void Deactivate(string widgetId)
    {
        try
        {
            DeleteWidget(widgetId, "");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Deactivate failed: {ex.Message}");
        }
    }

    private void OnProviderDataChanged(object? sender, EventArgs e) => RefreshAllWidgets();

    private void EnsureTimerRunning()
    {
        lock (_lock)
        {
            if (_refreshTimer == null)
            {
                var interval = TimeSpan.FromSeconds(30);
                _refreshTimer = new Timer(_ => RefreshAllWidgets(), null, interval, interval);
            }
        }
    }

    private void StopTimerIfNoWidgets()
    {
        lock (_lock)
        {
            if (_activeWidgets.Count == 0)
            {
                _refreshTimer?.Dispose();
                _refreshTimer = null;
            }
        }
    }

    private void RefreshAllWidgets()
    {
        foreach (var widgetId in _activeWidgets.Keys)
        {
            try
            {
                UpdateWidget(widgetId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshAllWidgets: {widgetId} failed: {ex.Message}");
            }
        }
    }

    private void UpdateWidget(string widgetId)
    {
        try
        {
            if (!_activeWidgets.TryGetValue(widgetId, out var state))
                return;

            if (!_providers.TryGetValue(state.DefinitionId, out var provider))
                return;

            var usage = provider.FetchUsageAsync(CancellationToken.None).GetAwaiter().GetResult();

            var templateJson = GetTemplate(state.Size);
            var dataJson = BuildDataJson(usage, provider.DisplayName, state.Size);

            var updateOptions = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = templateJson,
                Data = dataJson
            };

            WidgetManager.GetDefault().UpdateWidget(updateOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UpdateWidget failed: {ex.Message}");
        }
    }

    private static string BuildDataJson(DataUsageSnapshot usage, string providerName, WidgetSize size)
    {
        var hasData = usage.FetchedAt != DateTimeOffset.MinValue;
        var util = usage.Utilization;            // 0..1, or null when unlimited/unknown
        var utilPercent = util.HasValue ? util.Value * 100.0 : 0.0;

        var (barStyle, statusColor) = GetStatusVisuals(utilPercent, util.HasValue);

        // Headline "remaining" figure.
        string remainingText;
        if (!hasData)
            remainingText = "No data";
        else if (usage.IsUnlimited)
            remainingText = "Unlimited";
        else if (usage.RemainingGb is { } rem)
            remainingText = $"{FormatGb(rem)} left";
        else
            remainingText = $"{FormatGb(usage.UsedGb)} used";

        // Secondary "used of total" line.
        string usedText;
        if (!hasData)
            usedText = usage.Message ?? "Run the collector to sign in.";
        else if (usage.IsUnlimited)
            usedText = $"{FormatGb(usage.UsedGb)} used";
        else if (usage.TotalGb is { } total)
            usedText = $"{FormatGb(usage.UsedGb)} of {FormatGb(total)} used";
        else
            usedText = $"{FormatGb(usage.UsedGb)} used";

        var percentText = util.HasValue ? $"{utilPercent:F0}%" : "—%";
        var percentValueClamped = util.HasValue ? Math.Max(1, Math.Min(100, (int)Math.Round(utilPercent))) : 0;
        var percentRemaining = Math.Max(1, 100 - percentValueClamped);

        // Reset / cycle info (days + hours).
        var resetText = "";
        if (usage.CycleResetsAt is { } reset)
        {
            var remaining = reset - DateTimeOffset.Now;
            if (remaining.TotalSeconds <= 0)
                resetText = "Cycle ended";
            else
            {
                var days = (int)remaining.TotalDays;
                var span = days > 0
                    ? $"{days}d {remaining.Hours}h"
                    : $"{remaining.Hours}h {remaining.Minutes}m";
                resetText = $"Resets {reset.LocalDateTime:MMM d} ({span})";
            }
        }

        // Status / staleness banner (empty when fresh).
        var statusMessage = usage.Message ?? "";

        // Current connection (tethering) status.
        var net = NetworkDetector.Detect();
        string connectionText;
        if (!net.IsConnected)
            connectionText = "Offline";
        else if (net.IsTethered)
            connectionText = string.IsNullOrWhiteSpace(net.NetworkName)
                ? "Tethered"
                : $"Tethered via {net.NetworkName}";
        else
            connectionText = string.IsNullOrWhiteSpace(net.NetworkName)
                ? "Not on mobile"
                : $"On {net.NetworkName} (not mobile)";

        return $$"""
        {
            "providerName": "{{Escape(providerName)}}",
            "planName": "{{Escape(usage.PlanName)}}",
            "remainingText": "{{Escape(remainingText)}}",
            "usedText": "{{Escape(usedText)}}",
            "percentText": "{{percentText}}",
            "percentValueClamped": {{percentValueClamped}},
            "percentRemaining": {{percentRemaining}},
            "barStyle": "{{barStyle}}",
            "statusColor": "{{statusColor}}",
            "resetTime": "{{Escape(resetText)}}",
            "statusMessage": "{{Escape(statusMessage)}}",
            "connection": "{{Escape(connectionText)}}",
            "size": "{{size}}"
        }
        """;
    }

    private static string FormatGb(double gb)
    {
        if (gb >= 100) return $"{gb:F0} GB";
        if (gb >= 10) return $"{gb:F1} GB";
        return $"{gb:F2} GB";
    }

    private static string Escape(string? s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static (string barStyle, string statusColor) GetStatusVisuals(double utilization, bool hasData)
    {
        if (!hasData) return ("emphasis", "default");
        return utilization switch
        {
            > 90 => ("attention", "attention"),
            > 75 => ("warning", "warning"),
            _ => ("accent", "accent")
        };
    }

    private static string GetTemplate(WidgetSize size)
    {
        var templateName = size switch
        {
            WidgetSize.Small => "data-small.json",
            WidgetSize.Medium => "data-medium.json",
            WidgetSize.Large => "data-large.json",
            _ => "data-medium.json"
        };
        return AdaptiveCardTemplateLoader.LoadTemplate(templateName);
    }
}
