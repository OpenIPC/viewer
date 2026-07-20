using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace OpenIPC.Viewer.Web.Localization;

// Per-request UI language: the `lang` cookie wins (set by the switcher), else the
// browser's Accept-Language. Scoped so each server-rendered request resolves once.
public sealed class WebLocalizer
{
    public const string LanguageCookie = "lang";

    private readonly IReadOnlyDictionary<string, string> _dict;

    public WebLocalizer(IHttpContextAccessor accessor)
    {
        Language = Resolve(accessor.HttpContext);
        _dict = Language == "ru" ? WebStrings.Ru : WebStrings.En;
    }

    public string Language { get; }

    public string this[string key] => _dict.TryGetValue(key, out var value) ? value : key;

    private static string Resolve(HttpContext? ctx)
    {
        if (ctx is null)
            return "en";
        if (ctx.Request.Cookies.TryGetValue(LanguageCookie, out var c) && (c == "ru" || c == "en"))
            return c;
        var accept = ctx.Request.Headers.AcceptLanguage.ToString();
        return accept.Contains("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
    }
}
