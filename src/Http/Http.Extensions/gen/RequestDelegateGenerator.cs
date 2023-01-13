// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.AspNetCore.Http.Generators.StaticRouteHandlerModel;

namespace Microsoft.AspNetCore.Http.Generators;

[Generator]
public sealed class RequestDelegateGenerator : IIncrementalGenerator
{
    private static readonly string[] _knownMethods =
    {
        "MapGet",
        "MapPost",
        "MapPut",
        "MapDelete",
        "MapPatch",
        "Map",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var endpoints = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: (node, _) => node is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name: IdentifierNameSyntax
                    {
                        Identifier: { ValueText: var method }
                    }
                },
                ArgumentList: { Arguments: { Count: 2 } args }
            } && _knownMethods.Contains(method),
            transform: (context, token) =>
            {
                var operation = context.SemanticModel.GetOperation(context.Node, token) as IInvocationOperation;
                var wellKnownTypes = WellKnownTypes.GetOrCreate(context.SemanticModel.Compilation);
                return StaticRouteHandlerModelParser
                    .WithEndpoint(operation, wellKnownTypes, context.SemanticModel)
                    .WithHttpMethod()
                    .WithEndpointRoute()
                    .WithEndpointResponse();
            })
            .WithTrackingName("EndpointModel");

        context.RegisterSourceOutput(endpoints, (context, endpoint) =>
        {
            var (filePath, _) = endpoint.Location;
            foreach (var diagnostic in endpoint.Diagnostics)
            {
                context.ReportDiagnostic(Diagnostic.Create(diagnostic, endpoint.Operation.Syntax.GetLocation(), filePath));
            }
        });

        var thunks = endpoints.Select((endpoint, _) => $$"""
        [{{endpoint.EmitSourceKey()}}] = (
           (del, builder) =>
            {
                builder.Metadata.Add(new SourceKey{{endpoint.EmitSourceKey()}});
            },
            (del, builder) =>
            {
                var handler = ({{endpoint.EmitHandlerDelegateType()}})del;
                EndpointFilterDelegate? filteredInvocation = null;

                if (builder.FilterFactories.Count > 0)
                {
                    filteredInvocation = GeneratedRouteBuilderExtensionsCore.BuildFilterDelegate(ic =>
                    {
                        if (ic.HttpContext.Response.StatusCode == 400)
                        {
                            return System.Threading.Tasks.ValueTask.FromResult<object?>(Results.Empty);
                        }
                        {{endpoint.EmitFilteredInvocation()}}
                    },
                    builder,
                    handler.Method);
                }

                {{endpoint.EmitRequestHandler()}}
                {{StaticRouteHandlerModelEmitter.EmitFilteredRequestHandler()}}

                return filteredInvocation is null ? RequestHandler : RequestHandlerFiltered;
            }),
""");

        var stronglyTypedEndpointDefinitions = endpoints.Select((endpoint, _) => $$"""
{{RequestDelegateGeneratorSources.GeneratedCodeAttribute}}
internal static Microsoft.AspNetCore.Builder.RouteHandlerBuilder {{endpoint.HttpMethod}}(
        this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,
        [System.Diagnostics.CodeAnalysis.StringSyntax("Route")] string pattern,
        {{endpoint.EmitHandlerDelegateType()}} handler,
        [System.Runtime.CompilerServices.CallerFilePath] string filePath = "",
        [System.Runtime.CompilerServices.CallerLineNumber]int lineNumber = 0)
    {
        return GeneratedRouteBuilderExtensionsCore.MapCore(endpoints, pattern, handler, GetVerb, filePath, lineNumber);
    }
""");

        var thunksAndEndpoints = thunks.Collect().Combine(stronglyTypedEndpointDefinitions.Collect());

        context.RegisterSourceOutput(thunksAndEndpoints, (context, sources) =>
        {
            var (thunks, endpoints) = sources;

            var endpointsCode = new StringBuilder();
            var thunksCode = new StringBuilder();
            foreach (var endpoint in endpoints)
            {
                endpointsCode.AppendLine(endpoint);
            }
            foreach (var thunk in thunks)
            {
                thunksCode.AppendLine(thunk);
            }

            var code = RequestDelegateGeneratorSources.GetGeneratedRouteBuilderExtensionsSource(
                genericThunks: string.Empty,
                thunks: thunksCode.ToString(),
                endpoints: endpointsCode.ToString());
            context.AddSource("GeneratedRouteBuilderExtensions.g.cs", code);
        });
    }
}
