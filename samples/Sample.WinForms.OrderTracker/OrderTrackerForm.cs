// Sample.WinForms.OrderTracker — main form
//
// Code-only Form (no .designer.cs), single ListView + status panel.
// Subscribes to OrderViewModel.PropertyChanged + Orders CollectionChanged
// so the UI stays in sync when the LLM mutates state via [McpCallable].

using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Sample.WinForms.OrderTracker;

public sealed class OrderTrackerForm : Form
{
    private readonly OrderViewModel _vm;
    private readonly ListView _list = new();
    private readonly Label _totalLabel = new();
    private readonly Label _newLabel = new();
    private readonly Label _revenueLabel = new();
    private readonly Label _filterLabel = new();
    private readonly Button _addBtn = new();
    private readonly Button _promoteBtn = new();
    private readonly Button _cancelBtn = new();
    private readonly Button _clearBtn = new();
    private readonly TextBox _customerBox = new();
    private readonly TextBox _amountBox = new();

    public OrderTrackerForm() : this(OrderViewModel.Shared) { }

    public OrderTrackerForm(OrderViewModel vm)
    {
        _vm = vm;
        Text = "Order Tracker — Marionette WinForms Showcase";
        ClientSize = new Size(720, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildLayout();

        _list.FullRowSelect = true;
        _list.View = View.Details;
        _list.Columns.Add("ID", 50);
        _list.Columns.Add("Customer", 200);
        _list.Columns.Add("Amount", 100);
        _list.Columns.Add("Status", 110);
        _list.Columns.Add("Created (UTC)", 180);
        _list.GridLines = true;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Orders.CollectionChanged += (_, _) => SafeBeginInvoke(RefreshList);
        Shown += (_, _) => { RefreshLabels(); RefreshList(); };
        FormClosed += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;

        _addBtn.Click += (_, _) =>
        {
            if (decimal.TryParse(_amountBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amt) &&
                !string.IsNullOrWhiteSpace(_customerBox.Text))
            {
                _vm.AddOrder(_customerBox.Text.Trim(), amt);
                _customerBox.Text = string.Empty;
                _amountBox.Text = string.Empty;
                _customerBox.Focus();
            }
        };
        _promoteBtn.Click += (_, _) => InvokeOnSelectedId(_vm.PromoteOrder);
        _cancelBtn.Click += (_, _) => InvokeOnSelectedId(_vm.CancelOrder);
        _clearBtn.Click += (_, _) => _vm.ClearCompleted();
    }

    private void BuildLayout()
    {
        // Top stats panel
        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(12, 8, 12, 8),
        };

        _totalLabel.Name = "TotalOrdersLabel";
        _totalLabel.AutoSize = true;
        _totalLabel.Location = new Point(12, 12);
        _newLabel.Name = "NewOrdersLabel";
        _newLabel.AutoSize = true;
        _newLabel.Location = new Point(180, 12);
        _revenueLabel.Name = "RevenueLabel";
        _revenueLabel.AutoSize = true;
        _revenueLabel.Location = new Point(360, 12);
        _filterLabel.Name = "FilterLabel";
        _filterLabel.AutoSize = true;
        _filterLabel.Location = new Point(540, 12);

        topPanel.Controls.Add(_totalLabel);
        topPanel.Controls.Add(_newLabel);
        topPanel.Controls.Add(_revenueLabel);
        topPanel.Controls.Add(_filterLabel);
        Controls.Add(topPanel);

        // Bottom action panel
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            Padding = new Padding(12, 8, 12, 8),
        };

        var custLbl = new Label { Text = "Customer:", Location = new Point(12, 12), AutoSize = true };
        _customerBox.Name = "CustomerInput";
        _customerBox.Location = new Point(78, 9);
        _customerBox.Width = 160;

        var amtLbl = new Label { Text = "Amount:", Location = new Point(248, 12), AutoSize = true };
        _amountBox.Name = "AmountInput";
        _amountBox.Location = new Point(304, 9);
        _amountBox.Width = 80;

        _addBtn.Name = "AddOrderButton";
        _addBtn.Text = "Add";
        _addBtn.Location = new Point(394, 7);
        _addBtn.AutoSize = true;

        _promoteBtn.Name = "PromoteOrderButton";
        _promoteBtn.Text = "Promote";
        _promoteBtn.Location = new Point(454, 7);
        _promoteBtn.AutoSize = true;

        _cancelBtn.Name = "CancelOrderButton";
        _cancelBtn.Text = "Cancel";
        _cancelBtn.Location = new Point(534, 7);
        _cancelBtn.AutoSize = true;

        _clearBtn.Name = "ClearCompletedButton";
        _clearBtn.Text = "Clear ✓✗";
        _clearBtn.Location = new Point(606, 7);
        _clearBtn.AutoSize = true;

        bottomPanel.Controls.Add(custLbl);
        bottomPanel.Controls.Add(_customerBox);
        bottomPanel.Controls.Add(amtLbl);
        bottomPanel.Controls.Add(_amountBox);
        bottomPanel.Controls.Add(_addBtn);
        bottomPanel.Controls.Add(_promoteBtn);
        bottomPanel.Controls.Add(_cancelBtn);
        bottomPanel.Controls.Add(_clearBtn);
        Controls.Add(bottomPanel);

        // Center list
        _list.Name = "OrdersList";
        _list.Dock = DockStyle.Fill;
        Controls.Add(_list);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => SafeBeginInvoke(RefreshLabels);

    private void RefreshLabels()
    {
        _totalLabel.Text = $"Total orders: {_vm.TotalOrders}";
        _newLabel.Text = $"New: {_vm.NewOrders}";
        _revenueLabel.Text = $"Revenue: {_vm.TotalRevenue:C}";
        _filterLabel.Text = $"Filter: {(string.IsNullOrEmpty(_vm.StatusFilter) ? "(none)" : _vm.StatusFilter)}";
    }

    private void RefreshList()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var o in _vm.Orders)
            {
                var item = new ListViewItem(o.Id.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(o.Customer);
                item.SubItems.Add(o.Amount.ToString("C", CultureInfo.CurrentCulture));
                item.SubItems.Add(o.Status.ToString());
                item.SubItems.Add(o.Created.ToString("u", CultureInfo.InvariantCulture));
                item.Tag = o.Id;
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private void InvokeOnSelectedId(Func<int, bool> action)
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is int id) _ = action(id);
    }

    private void SafeBeginInvoke(Action a)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(a);
        else a();
    }
}
