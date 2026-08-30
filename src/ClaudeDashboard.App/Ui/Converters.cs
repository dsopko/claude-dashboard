using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Hides a count that is zero — "3 need you · 2 unread" rather than a strip of noughts.
/// </summary>
/// <remarks>
/// A converter rather than a bool on the view model, because "is this number zero" is a question
/// about presentation and putting five more properties on the view model to answer it would be
/// the layout dictating the model's shape.
/// </remarks>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class ZeroToCollapsedConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count != 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this converts one way.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility does not convert back to a count.");
}

/// <summary>
/// The other half of the Grouped/Flat pair: one flag, two buttons, no second source of truth.
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : DependencyProperty.UnsetValue;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag ? !flag : DependencyProperty.UnsetValue;
}

/// <summary>Hides an element whose text is empty — the group tag on a session with no workspace.</summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this converts one way.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility does not convert back to text.");
}

/// <summary>
/// <see langword="true"/> collapses; <see langword="false"/> shows. The mirror of WPF's own
/// <c>BooleanToVisibilityConverter</c>.
/// </summary>
/// <remarks>
/// Needed because two header controls swap places on one flag — the "Select" button and the
/// selection strip — and only one of them can use the built-in converter. Composing
/// <c>InverseBoolean</c> with the built-in is not possible in a single binding.
/// </remarks>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility does not convert back to a flag.");
}
