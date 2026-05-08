// Sample.WinForms.OrderTracker — order list view-model
//
// Mirrors the shape of TodoListViewModel from Sample.Wpf.TodoApp:
//   * [McpRoot] on the class.
//   * 5 [McpCallable] methods (Add / Promote / Cancel / Clear / SetFilter).
//   * 4 [McpObservable] properties.
//   * 1 [McpEvent] (typed args).
//   * Static singleton so the runtime's RootDescriptor.Create factory and
//     the form's data source point at the same instance.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

using Marionette;

namespace Sample.WinForms.OrderTracker;

public sealed class OrderShippedEventArgs : EventArgs
{
    public OrderShippedEventArgs(int id, string customer, decimal amount)
    {
        Id = id;
        Customer = customer;
        Amount = amount;
    }
    public int Id { get; }
    public string Customer { get; }
    public decimal Amount { get; }
}

[McpRoot]
public sealed class OrderViewModel : INotifyPropertyChanged
{
    private static OrderViewModel? s_shared;
    private static readonly object s_sharedLock = new();

    public static OrderViewModel Shared
    {
        get
        {
            if (s_shared is not null) return s_shared;
            lock (s_sharedLock)
            {
                s_shared ??= new OrderViewModel();
                return s_shared;
            }
        }
    }

    private readonly ObservableCollection<Order> _orders = new();
    private int _nextId = 1;
    private OrderStatus? _filter;

    public OrderViewModel()
    {
        // Seed with a few demo rows so the form has something to show.
        _orders.Add(new Order(_nextId++, "Acme Corp", 142.50m, OrderStatus.New, DateTime.UtcNow.AddMinutes(-30)));
        _orders.Add(new Order(_nextId++, "Globex", 89.99m, OrderStatus.Processing, DateTime.UtcNow.AddMinutes(-15)));
        _orders.Add(new Order(_nextId++, "Initech", 1250.00m, OrderStatus.New, DateTime.UtcNow.AddMinutes(-2)));
        s_shared ??= this;
    }

    /// <summary>Underlying list bound to the ListView. Not exposed via [McpObservable].</summary>
    public ObservableCollection<Order> Orders => _orders;

    // ------------------- [McpObservable] surface -------------------

    [McpObservable("Total number of orders currently tracked.", Watchable = true)]
    public int TotalOrders => _orders.Count;

    [McpObservable("Number of orders in 'New' status.", Watchable = true)]
    public int NewOrders => _orders.Count(o => o.Status == OrderStatus.New);

    [McpObservable("Sum of amounts across all non-cancelled orders.", Watchable = true)]
    public decimal TotalRevenue => _orders.Where(o => o.Status != OrderStatus.Cancelled).Sum(o => o.Amount);

    [McpObservable("Active status filter; null means show all.", Watchable = true)]
    public string? StatusFilter => _filter?.ToString();

    // ------------------- [McpEvent] surface -------------------

    [McpEvent("Fires whenever an order is promoted to Shipped status.")]
    public event EventHandler<OrderShippedEventArgs>? OrderShipped;

    // ------------------- [McpCallable] surface -------------------

    [McpCallable("Add a new order. Returns the assigned order id.")]
    public int AddOrder(string customer, decimal amount)
    {
        var id = _nextId++;
        _orders.Add(new Order(id, customer, amount, OrderStatus.New, DateTime.UtcNow));
        RaiseDerived();
        return id;
    }

    [McpCallable("Promote an order to the next status (New → Processing → Shipped).")]
    public bool PromoteOrder(int orderId)
    {
        var idx = IndexOf(orderId);
        if (idx < 0) return false;
        var o = _orders[idx];
        var next = o.Status switch
        {
            OrderStatus.New => OrderStatus.Processing,
            OrderStatus.Processing => OrderStatus.Shipped,
            _ => o.Status,
        };
        if (next == o.Status) return false;
        _orders[idx] = o with { Status = next };
        if (next == OrderStatus.Shipped)
        {
            OrderShipped?.Invoke(this, new OrderShippedEventArgs(o.Id, o.Customer, o.Amount));
        }
        RaiseDerived();
        return true;
    }

    [McpCallable("Cancel an order (sets status to Cancelled).")]
    public bool CancelOrder(int orderId)
    {
        var idx = IndexOf(orderId);
        if (idx < 0) return false;
        var o = _orders[idx];
        if (o.Status == OrderStatus.Cancelled) return false;
        _orders[idx] = o with { Status = OrderStatus.Cancelled };
        RaiseDerived();
        return true;
    }

    [McpCallable("Remove all orders that are Shipped or Cancelled.")]
    public int ClearCompleted()
    {
        var removed = 0;
        for (var i = _orders.Count - 1; i >= 0; i--)
        {
            if (_orders[i].Status is OrderStatus.Shipped or OrderStatus.Cancelled)
            {
                _orders.RemoveAt(i);
                removed++;
            }
        }
        if (removed > 0) RaiseDerived();
        return removed;
    }

    [McpCallable("Set the active status filter. Pass null or an empty string to clear.")]
    public void SetFilter(string? status)
    {
        OrderStatus? next = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed))
        {
            next = parsed;
        }
        if (_filter == next) return;
        _filter = next;
        OnPropertyChanged(nameof(StatusFilter));
    }

    private int IndexOf(int id)
    {
        for (var i = 0; i < _orders.Count; i++)
        {
            if (_orders[i].Id == id) return i;
        }
        return -1;
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(TotalOrders));
        OnPropertyChanged(nameof(NewOrders));
        OnPropertyChanged(nameof(TotalRevenue));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}
