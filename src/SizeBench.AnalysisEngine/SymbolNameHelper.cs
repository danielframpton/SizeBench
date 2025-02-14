﻿using System.Text;
using SizeBench.AnalysisEngine.Symbols;

namespace SizeBench.AnalysisEngine;

public static class SymbolNameHelper
{
    public static string FunctionToGenericTemplatedName(IFunctionCodeSymbol function)
    {
        // This is surprisingly complicated.  The first important question is whether these are free functions or member functions.

        // Consider some examples like these:
        // ctl::ComObject<DirectUI::ListBoxItemAutomationPeer>::CreateInstanceNoInit<DirectUI::DependencyObject>
        // ctl::ComObject<DirectUI::WebViewUnviewableContentIdentifiedEventArgs>::CreateInstance<DirectUI::WebViewUnviewableContentIdentifiedEventArgs>
        // ctl::ComObject<DirectUI::AdaptiveTrigger>::ComObject<DirectUI::AdaptiveTrigger>
        // DirectUI::FreeFunction<ABC>
        // ns::SomeType::TemplatedFunction<ABC>
        //
        // In the first 3 cases, we have a templated type, with a templated function - the 4th one is a free function in a
        // namespace but it's templated, and the last one is a non-templated type in a namespace with a templated function.
        //
        // This presents a challenge - the type and namespace cases look essentially identical when just looking at these
        // strings.  So we have to also load the UDTs for the binary to see which ones correspond to types vs. namespaces.
        //
        // But beyond that, finding the right "::" to split on to have the left-hand side be the namespace/type and the
        // right-hand side be the function, is hard.  We have to walk from left to right, counting the '<'s we find and
        // match them up to '>'s

        ArgumentNullException.ThrowIfNull(function);

        GenericizeNamespaceAndTypeName(function.FormattedName.IncludeParentType, out var templateParamAnonymizedNames, out var segments);

        var typeOrNamespace = String.Empty;
        var functionSegment = segments[^1];
        for (var segmentIndex = 0; segmentIndex < segments.Count - 1; segmentIndex++)
        {
            typeOrNamespace += segments[segmentIndex];
            if (segmentIndex < segments.Count - 2)
            {
                typeOrNamespace += "::";
            }
        }

        // Now calculate the arguments, but with the anonymized names above (to enable proper grouping across the template instantiations)
        // TODO: TemplateFoldability: see about refactoring this somewhere since it's shared between FunctionSymbol and here...
        var sb = new StringBuilder();
        sb.Append('(');
        if (function.FunctionType?.ArgumentTypes != null)
        {
            for (var argumentIndex = 0; argumentIndex < function.FunctionType.ArgumentTypes.Count; argumentIndex++)
            {
                // Separate arguments with a comma and a space
                if (argumentIndex > 0)
                {
                    sb.Append(", ");
                }

                var argTypeName = function.FunctionType.ArgumentTypes[argumentIndex].Name;

                if (templateParamAnonymizedNames.ContainsKey(argTypeName))
                {
                    sb.Append(templateParamAnonymizedNames[argTypeName]);
                }
                else
                {
                    // We haven't seen this argument type before exactly, but we could have "DirectUI::AddPagesEventArgs" mapped to "T1" above, and this could be
                    // "ctl::ActivationFactory<DirectUI::AddPagesEventArgs>**" as the name.  So we should loop and replace with our existing T1/T2/etc... above
                    foreach (var anonymizedType in templateParamAnonymizedNames)
                    {
                        argTypeName = argTypeName.Replace(anonymizedType.Key, anonymizedType.Value, StringComparison.Ordinal);
                    }

                    sb.Append(argTypeName);
                }
            }
        }
        sb.Append(')');

        if (function.FunctionType?.IsConst == true)
        {
            sb.Append(" const");
        }

        if (function.FunctionType?.IsVolatile == true)
        {
            sb.Append(" volatile");
        }

        var finalGroupName = typeOrNamespace + (String.IsNullOrEmpty(typeOrNamespace) ? String.Empty : "::") + functionSegment + sb.ToString();

        return finalGroupName;
    }

    public static string UserDefinedTypeToGenericTemplatedName(UserDefinedTypeSymbol udt)
    {
        ArgumentNullException.ThrowIfNull(udt);

        return GenericizeNamespaceAndTypeName(udt.Name, out _, out _);
    }

    private static string GenericizeNamespaceAndTypeName(string name, out Dictionary<string, string> templateParamAnonymizedNames, out List<string> segments)
    {
        segments = new List<string>();
        var sb = new StringBuilder();
        var templateDepth = 0;
        var templateParameterCount = 0;
        var templateParamTotalCountAcrossAllSegments = 0;
        var templateParamStartIndex = -1;
        var templateParamConcreteNames = new Dictionary<int, string>();
        templateParamAnonymizedNames = new Dictionary<string, string>();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '<')
            {
                if (templateDepth == 0)
                {
                    templateParamStartIndex = i + 1;
                    templateParameterCount = 1;
                }

                templateDepth++;
            }
            else if (c == '>')
            {
                templateDepth--;
                if (templateDepth == 0)
                {
                    templateParamConcreteNames.Add(templateParameterCount, name[templateParamStartIndex..i].Trim());

                    sb.Append('<');
                    for (var paramCount = 1; paramCount <= templateParameterCount; paramCount++)
                    {
                        if (paramCount > 1)
                        {
                            sb.Append(',');
                        }

                        var anonymizedNameToAppend = String.Empty;

                        // If we've seen this name before, re-use the "TX" that we assigned it before.
                        // If not, we'll establish a "TX" for it.
                        if (templateParamAnonymizedNames.ContainsKey(templateParamConcreteNames[paramCount]))
                        {
                            anonymizedNameToAppend = templateParamAnonymizedNames[templateParamConcreteNames[paramCount]];
                        }
                        else
                        {
                            templateParamTotalCountAcrossAllSegments++;
                            if (templateParamConcreteNames[paramCount].Length > 0)
                            {
                                anonymizedNameToAppend = "T" + templateParamTotalCountAcrossAllSegments;
                                templateParamAnonymizedNames.Add(templateParamConcreteNames[paramCount], anonymizedNameToAppend);
                            }
                        }

                        sb.Append(anonymizedNameToAppend);
                    }
                    sb.Append('>');

                    templateParameterCount = 1;
                    templateParamConcreteNames.Clear();
                }
            }
            else if (c == ',')
            {
                if (templateDepth == 1)
                {
                    templateParamConcreteNames.Add(templateParameterCount, name[templateParamStartIndex..i].Trim());
                    templateParamStartIndex = i + 1;
                    templateParameterCount++;
                }
            }
            else if (c == ':' && i < name.Length - 1 && name[i + 1] == ':')
            {
                i++;
                if (templateDepth == 0)
                {
                    segments.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else if (templateDepth == 0)
            {
                sb.Append(c);
            }
        }

        segments.Add(sb.ToString());

        var typeOrNamespace = String.Empty;
        var functionOrTypeNameSegment = segments[^1];
        for (var segmentIndex = 0; segmentIndex < segments.Count - 1; segmentIndex++)
        {
            typeOrNamespace += segments[segmentIndex];
            if (segmentIndex < segments.Count - 2)
            {
                typeOrNamespace += "::";
            }
        }

        var finalGroupName = typeOrNamespace + (String.IsNullOrEmpty(typeOrNamespace) ? String.Empty : "::") + functionOrTypeNameSegment;

        return finalGroupName;
    }
}
