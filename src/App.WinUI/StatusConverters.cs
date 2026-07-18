using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using OtelWindowsHandoff.Pipeline;

namespace OtelWindowsHandoff.WinUI;

/// <summary>フェーズ状態を Fluent のセマンティック Brush へ変換します。</summary>
public sealed class PhaseStateToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is PipelineProgressState state
            ? state switch
            {
                PipelineProgressState.Running => "AccentFillColorDefaultBrush",
                PipelineProgressState.Succeeded => "SystemFillColorSuccessBrush",
                PipelineProgressState.Failed => "SystemFillColorCriticalBrush",
                _ => "ControlStrokeColorDefaultBrush",
            }
            : "ControlStrokeColorDefaultBrush";
        return Application.Current.Resources[key];
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

/// <summary>フェーズ状態を Segoe Fluent Icons のグリフへ変換します。</summary>
public sealed class PhaseStateToGlyphConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is PipelineProgressState state
            ? state switch
            {
                PipelineProgressState.Running => "\uE895",
                PipelineProgressState.Succeeded => "\uE73E",
                PipelineProgressState.Failed => "\uEA39",
                _ => "\uE823",
            }
            : "\uE823";
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

/// <summary>障害注入種別を行の識別色へ変換します。</summary>
public sealed class FaultModeToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is FaultMode fault && fault.HasFlag(FaultMode.AccessDenied)
            ? "SystemFillColorCriticalBrush"
            : value is FaultMode slowRead && slowRead.HasFlag(FaultMode.SlowRead)
                ? "SystemFillColorCautionBrush"
                : "SubtleFillColorTransparentBrush";
        return Application.Current.Resources[key];
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
