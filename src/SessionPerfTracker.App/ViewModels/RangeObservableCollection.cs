using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SessionPerfTracker.App.ViewModels;

/// <summary>
/// ObservableCollection that can swap its whole content with a single Reset notification.
/// Replacing hundreds of Global Watch rows one Add at a time is far more expensive for WPF.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ResetWith(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
