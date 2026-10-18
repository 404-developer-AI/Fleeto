using System.Collections.Concurrent;
using System.Reflection;
using Fleeto.Core.Performance;
using Microsoft.AspNetCore.Components;

namespace Fleeto.Web.Performance;

/// <summary>
/// The route templates of every page of web, read once from their <c>@page</c> directives, so a page interaction is keyed by its
/// template (<c>/clients/{ClientId:guid}</c>) and never by its URL (PERFORMANCE.md §4.1).
/// </summary>
public sealed class PageRoutes
{
    private readonly RouteTemplates _templates;
    private readonly ConcurrentDictionary<Type, string[]> _routesOfPage = new();
    private LastMatch? _last;

    public PageRoutes()
        : this(typeof(PageRoutes).Assembly)
    {
    }

    public PageRoutes(Assembly assembly)
    {
        _templates = new RouteTemplates(assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: false))
            .Select(r => r.Template));
    }

    public IReadOnlyList<string> All => _templates.All;

    /// <summary>The template of a URL. The last answer is kept, because the same circuit asks for the same URL many times.</summary>
    public string Match(string url)
    {
        if (Volatile.Read(ref _last) is { } last && string.Equals(last.Url, url, StringComparison.Ordinal))
        {
            return last.Template;
        }

        var template = _templates.Match(url);
        Volatile.Write(ref _last, new LastMatch(url, template));
        return template;
    }

    /// <summary>The template of a page component: the one of its routes that matches the URL, or its first.</summary>
    public string Of(Type pageType, string url)
    {
        var routes = _routesOfPage.GetOrAdd(pageType, type => type.GetCustomAttributes<RouteAttribute>(inherit: false)
            .Select(r => "/" + string.Join('/', r.Template.Split('/', StringSplitOptions.RemoveEmptyEntries)))
            .ToArray());
        if (routes.Length == 0)
        {
            return Match(url);
        }

        if (routes.Length == 1)
        {
            return routes[0];
        }

        var match = Match(url);
        return routes.FirstOrDefault(r => string.Equals(r, match, StringComparison.OrdinalIgnoreCase)) ?? routes[0];
    }

    private sealed record LastMatch(string Url, string Template);
}
