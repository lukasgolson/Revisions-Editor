using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RevisionsEditor.App.ViewModels;

/// <summary>
/// ObservableCollection that can swap its whole contents with a single Reset notification, so bound
/// grids re-render once instead of once per Clear/Add.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
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
