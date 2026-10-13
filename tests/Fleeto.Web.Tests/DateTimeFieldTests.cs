using Bunit;
using Fleeto.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Fleeto.Web.Tests;

/// <summary>
/// The date and time fields of Fleeto (0.6.0): the browser's own inputs, showing and reading values in the format of the input,
/// with their range; something that is not a valid value reads as empty.
/// </summary>
public sealed class DateTimeFieldTests : BunitContext
{
    public DateTimeFieldTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void The_time_field_shows_and_reads_a_time_of_day()
    {
        TimeSpan? value = new TimeSpan(20, 0, 0);
        var field = Render<TimeField>(p => p.Add(f => f.Value, value).Add(f => f.Label, "Starts at")
            .Add(f => f.ValueChanged, v => value = v));

        var input = field.Find("input");
        Assert.Equal("time", input.GetAttribute("type"));
        Assert.Equal("20:00", input.GetAttribute("value"));

        input.Change("06:30");
        Assert.Equal(new TimeSpan(6, 30, 0), value);
        input.Change("25:00");
        Assert.Null(value);
    }

    [Fact]
    public void The_date_and_time_field_shows_a_calendar_with_a_time_and_its_range()
    {
        DateTime? value = new DateTime(2026, 10, 13, 22, 0, 0);
        var field = Render<DateTimeField>(p => p.Add(f => f.Value, value).Add(f => f.Min, new DateTime(2026, 10, 13, 21, 5, 0))
            .Add(f => f.Max, new DateTime(2026, 10, 20, 21, 5, 0)).Add(f => f.ValueChanged, v => value = v));

        var input = field.Find("input");
        Assert.Equal("datetime-local", input.GetAttribute("type"));
        Assert.Equal("2026-10-13T22:00", input.GetAttribute("value"));
        Assert.Equal("2026-10-13T21:05", input.GetAttribute("min"));
        Assert.Equal("2026-10-20T21:05", input.GetAttribute("max"));

        input.Change("2026-10-15T08:45");
        Assert.Equal(new DateTime(2026, 10, 15, 8, 45, 0), value);
        Assert.Equal(DateTimeKind.Unspecified, value!.Value.Kind);
        input.Change("");
        Assert.Null(value);
    }

    [Fact]
    public void The_date_field_shows_and_reads_a_date()
    {
        DateTime? value = null;
        var field = Render<DateField>(p => p.Add(f => f.Value, value).Add(f => f.ValueChanged, v => value = v));

        var input = field.Find("input");
        Assert.Equal("date", input.GetAttribute("type"));

        input.Change("2027-01-31");
        Assert.Equal(new DateTime(2027, 1, 31), value);
    }
}
