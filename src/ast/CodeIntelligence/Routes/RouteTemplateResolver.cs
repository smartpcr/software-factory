// -----------------------------------------------------------------------
// <copyright file="RouteTemplateResolver.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Routes;

using System.Text.RegularExpressions;

/// <summary>
/// Resolves and parses route templates for HTTP endpoints.
/// </summary>
public sealed class RouteTemplateResolver
{
    private static readonly Regex RouteParameterPattern = new(
        @"\{(?<name>[^:?}]+)(?::(?<constraint>[^?}]+))?(?<optional>\?)?\}",
        RegexOptions.Compiled);

    private static readonly Regex PlaceholderPattern = new(
        @"\[(?<placeholder>controller|action|area)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a route template into its components.
    /// </summary>
    /// <param name="template">The route template.</param>
    /// <returns>Parsed route template.</returns>
    public ParsedRouteTemplate Parse(string template)
    {
        var parameters = new List<RouteParameter>();
        var segments = new List<RouteSegment>();

        var normalizedTemplate = template.TrimStart('/', '~');
        var parts = normalizedTemplate.Split('/');

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            // Check for catch-all parameter
            if (part.StartsWith("{*") || part.StartsWith("{**"))
            {
                var name = part.TrimStart('{', '*').TrimEnd('}');
                parameters.Add(new RouteParameter
                {
                    Name = name,
                    IsCatchAll = true,
                    IsOptional = false
                });

                segments.Add(new RouteSegment
                {
                    Value = part,
                    SegmentType = RouteSegmentType.CatchAll,
                    ParameterName = name
                });

                continue;
            }

            // Check for route parameter
            var match = RouteParameterPattern.Match(part);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                var constraint = match.Groups["constraint"].Success
                    ? match.Groups["constraint"].Value
                    : null;
                var isOptional = match.Groups["optional"].Success;

                parameters.Add(new RouteParameter
                {
                    Name = name,
                    Constraint = constraint,
                    IsOptional = isOptional,
                    ConstraintType = ParseConstraint(constraint)
                });

                segments.Add(new RouteSegment
                {
                    Value = part,
                    SegmentType = RouteSegmentType.Parameter,
                    ParameterName = name,
                    Constraint = constraint,
                    IsOptional = isOptional
                });
            }
            else if (PlaceholderPattern.IsMatch(part))
            {
                // [controller], [action], [area]
                var placeholder = PlaceholderPattern.Match(part).Groups["placeholder"].Value.ToLowerInvariant();

                segments.Add(new RouteSegment
                {
                    Value = part,
                    SegmentType = RouteSegmentType.Placeholder,
                    PlaceholderType = placeholder switch
                    {
                        "controller" => PlaceholderType.Controller,
                        "action" => PlaceholderType.Action,
                        "area" => PlaceholderType.Area,
                        _ => PlaceholderType.Unknown
                    }
                });
            }
            else
            {
                segments.Add(new RouteSegment
                {
                    Value = part,
                    SegmentType = RouteSegmentType.Literal
                });
            }
        }

        return new ParsedRouteTemplate
        {
            OriginalTemplate = template,
            NormalizedTemplate = normalizedTemplate,
            Segments = segments,
            Parameters = parameters,
            HasCatchAll = parameters.Any(p => p.IsCatchAll),
            HasOptionalParameters = parameters.Any(p => p.IsOptional)
        };
    }

    /// <summary>
    /// Combines a controller route with an action route.
    /// </summary>
    /// <param name="controllerRoute">The controller-level route.</param>
    /// <param name="actionRoute">The action-level route.</param>
    /// <returns>Combined route template.</returns>
    public string Combine(string? controllerRoute, string? actionRoute)
    {
        if (string.IsNullOrEmpty(actionRoute))
        {
            return controllerRoute ?? "";
        }

        // Absolute action route overrides controller route
        if (actionRoute.StartsWith("/") || actionRoute.StartsWith("~/"))
        {
            return actionRoute.TrimStart('~', '/');
        }

        if (string.IsNullOrEmpty(controllerRoute))
        {
            return actionRoute;
        }

        return $"{controllerRoute.TrimEnd('/')}/{actionRoute.TrimStart('/')}";
    }

    /// <summary>
    /// Resolves placeholders in a route template.
    /// </summary>
    /// <param name="template">The route template.</param>
    /// <param name="controllerName">The controller name.</param>
    /// <param name="actionName">The action name.</param>
    /// <param name="areaName">The area name (optional).</param>
    /// <returns>Resolved route template.</returns>
    public string ResolvePlaceholders(string template, string? controllerName, string? actionName, string? areaName = null)
    {
        var result = template;

        if (controllerName != null)
        {
            result = Regex.Replace(result, @"\[controller\]", controllerName.ToLowerInvariant(), RegexOptions.IgnoreCase);
        }

        if (actionName != null)
        {
            result = Regex.Replace(result, @"\[action\]", actionName.ToLowerInvariant(), RegexOptions.IgnoreCase);
        }

        if (areaName != null)
        {
            result = Regex.Replace(result, @"\[area\]", areaName.ToLowerInvariant(), RegexOptions.IgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Generates a regex pattern for matching URLs against this route.
    /// </summary>
    /// <param name="template">The route template.</param>
    /// <returns>Regex pattern for URL matching.</returns>
    public string GenerateMatchPattern(string template)
    {
        var parsed = this.Parse(template);
        var pattern = "^";

        foreach (var segment in parsed.Segments)
        {
            pattern += "/";

            switch (segment.SegmentType)
            {
                case RouteSegmentType.Literal:
                    pattern += Regex.Escape(segment.Value);
                    break;

                case RouteSegmentType.Parameter:
                    var paramPattern = GetConstraintPattern(segment.Constraint);
                    if (segment.IsOptional)
                    {
                        pattern += $"(?:{paramPattern})?";
                    }
                    else
                    {
                        pattern += paramPattern;
                    }

                    break;

                case RouteSegmentType.CatchAll:
                    pattern += ".*";
                    break;

                case RouteSegmentType.Placeholder:
                    pattern += "[^/]+";
                    break;
            }
        }

        pattern += "/?$";
        return pattern;
    }

    /// <summary>
    /// Extracts parameter values from a URL using a route template.
    /// </summary>
    /// <param name="template">The route template.</param>
    /// <param name="url">The URL to extract values from.</param>
    /// <returns>Dictionary of parameter names to values.</returns>
    public Dictionary<string, string> ExtractValues(string template, string url)
    {
        var values = new Dictionary<string, string>();
        var parsed = this.Parse(template);

        var templateParts = template.TrimStart('/').Split('/');
        var urlParts = url.TrimStart('/').Split('/');

        for (int i = 0; i < templateParts.Length && i < urlParts.Length; i++)
        {
            var match = RouteParameterPattern.Match(templateParts[i]);
            if (match.Success)
            {
                var paramName = match.Groups["name"].Value;
                values[paramName] = urlParts[i];
            }
        }

        return values;
    }

    private static RouteConstraintType? ParseConstraint(string? constraint)
    {
        if (string.IsNullOrEmpty(constraint))
        {
            return null;
        }

        return constraint.ToLowerInvariant() switch
        {
            "int" => RouteConstraintType.Int,
            "long" => RouteConstraintType.Long,
            "bool" => RouteConstraintType.Bool,
            "datetime" => RouteConstraintType.DateTime,
            "decimal" => RouteConstraintType.Decimal,
            "double" => RouteConstraintType.Double,
            "float" => RouteConstraintType.Float,
            "guid" => RouteConstraintType.Guid,
            "alpha" => RouteConstraintType.Alpha,
            "minlength" or "maxlength" or "length" => RouteConstraintType.String,
            "min" or "max" or "range" => RouteConstraintType.Numeric,
            "regex" => RouteConstraintType.Regex,
            _ => RouteConstraintType.Custom
        };
    }

    private static string GetConstraintPattern(string? constraint)
    {
        if (string.IsNullOrEmpty(constraint))
        {
            return "[^/]+";
        }

        return constraint.ToLowerInvariant() switch
        {
            "int" => @"-?\d+",
            "long" => @"-?\d+",
            "bool" => @"true|false",
            "guid" => @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            "alpha" => @"[a-zA-Z]+",
            "decimal" or "double" or "float" => @"-?\d+\.?\d*",
            _ => "[^/]+"
        };
    }
}

/// <summary>
/// Parsed route template.
/// </summary>
public record ParsedRouteTemplate
{
    /// <summary>Gets the original template.</summary>
    public required string OriginalTemplate { get; init; }

    /// <summary>Gets the normalized template.</summary>
    public required string NormalizedTemplate { get; init; }

    /// <summary>Gets the segments.</summary>
    public required IReadOnlyList<RouteSegment> Segments { get; init; }

    /// <summary>Gets the parameters.</summary>
    public required IReadOnlyList<RouteParameter> Parameters { get; init; }

    /// <summary>Gets whether the route has a catch-all parameter.</summary>
    public bool HasCatchAll { get; init; }

    /// <summary>Gets whether the route has optional parameters.</summary>
    public bool HasOptionalParameters { get; init; }
}

/// <summary>
/// A segment of a route template.
/// </summary>
public record RouteSegment
{
    /// <summary>Gets the raw value.</summary>
    public required string Value { get; init; }

    /// <summary>Gets the segment type.</summary>
    public required RouteSegmentType SegmentType { get; init; }

    /// <summary>Gets the parameter name (if parameter segment).</summary>
    public string? ParameterName { get; init; }

    /// <summary>Gets the constraint (if parameter segment).</summary>
    public string? Constraint { get; init; }

    /// <summary>Gets whether the segment is optional.</summary>
    public bool IsOptional { get; init; }

    /// <summary>Gets the placeholder type (if placeholder segment).</summary>
    public PlaceholderType? PlaceholderType { get; init; }
}

/// <summary>
/// A route parameter.
/// </summary>
public record RouteParameter
{
    /// <summary>Gets the parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the constraint string.</summary>
    public string? Constraint { get; init; }

    /// <summary>Gets the constraint type.</summary>
    public RouteConstraintType? ConstraintType { get; init; }

    /// <summary>Gets whether the parameter is optional.</summary>
    public bool IsOptional { get; init; }

    /// <summary>Gets whether this is a catch-all parameter.</summary>
    public bool IsCatchAll { get; init; }
}

/// <summary>
/// Types of route segments.
/// </summary>
public enum RouteSegmentType
{
    /// <summary>Literal text.</summary>
    Literal,

    /// <summary>Route parameter.</summary>
    Parameter,

    /// <summary>Catch-all parameter.</summary>
    CatchAll,

    /// <summary>Placeholder like [controller].</summary>
    Placeholder
}

/// <summary>
/// Types of route placeholders.
/// </summary>
public enum PlaceholderType
{
    /// <summary>Unknown placeholder.</summary>
    Unknown,

    /// <summary>[controller] placeholder.</summary>
    Controller,

    /// <summary>[action] placeholder.</summary>
    Action,

    /// <summary>[area] placeholder.</summary>
    Area
}

/// <summary>
/// Types of route constraints.
/// </summary>
public enum RouteConstraintType
{
    /// <summary>Integer constraint.</summary>
    Int,

    /// <summary>Long constraint.</summary>
    Long,

    /// <summary>Boolean constraint.</summary>
    Bool,

    /// <summary>DateTime constraint.</summary>
    DateTime,

    /// <summary>Decimal constraint.</summary>
    Decimal,

    /// <summary>Double constraint.</summary>
    Double,

    /// <summary>Float constraint.</summary>
    Float,

    /// <summary>GUID constraint.</summary>
    Guid,

    /// <summary>Alphabetic constraint.</summary>
    Alpha,

    /// <summary>String length constraint.</summary>
    String,

    /// <summary>Numeric range constraint.</summary>
    Numeric,

    /// <summary>Regex constraint.</summary>
    Regex,

    /// <summary>Custom constraint.</summary>
    Custom
}
