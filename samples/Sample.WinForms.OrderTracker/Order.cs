// Sample.WinForms.OrderTracker — simple Order data class.
// Plain INPC-free POCO; the ViewModel manages mutability and observables.

using System;

namespace Sample.WinForms.OrderTracker;

/// <summary>
/// One row in the OrderTracker app. Status flips through New → Processing →
/// Shipped via the Promote* callables.
/// </summary>
public sealed record Order(int Id, string Customer, decimal Amount, OrderStatus Status, DateTime Created);

public enum OrderStatus
{
    New,
    Processing,
    Shipped,
    Cancelled,
}
