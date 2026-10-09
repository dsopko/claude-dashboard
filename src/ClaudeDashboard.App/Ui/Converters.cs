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

/// <summary>Shows an element when every value is <see langword="true"/>, and collapses it otherwise.</summary>
/// <remarks>
/// For the usage on the row under the caption (MOD.7): it is there when the window has a reading AND the
/// caption's usage strip dropped a figure. The first value guards the second: a collapsed strip is not
/// measured, so its <see cref="FittingStrip.HasDropped"/> keeps whatever it last was.
/// </remarks>
public sealed class AllTrueToVisibleConverter : IMultiValueConverter
{
    /// <inheritdoc/>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 0 && values.All(value => value is true) ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this converts one way.</exception>
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility does not convert back to flags.");
}

/// <summary>Shows an element when any value is <see cref="Visibility.Visible"/>, and collapses it otherwise.</summary>
/// <remarks>
/// For the row under the caption (MOD.7): it is up when the counts or the usage are on it, and it is read off
/// the two strips' own visibility so the row and what it holds cannot disagree.
/// </remarks>
public sealed class AnyVisibleConverter : IMultiValueConverter
{
    /// <inheritdoc/>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Any(value => value is Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this converts one way.</exception>
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility does not convert back to visibilities.");
}
