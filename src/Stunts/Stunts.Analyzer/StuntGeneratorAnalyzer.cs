﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Stunts.Properties;

namespace Stunts
{
    /// <summary>
    /// Analyzes source code looking for method invocations to methods annotated with 
    /// the <see cref="StuntGeneratorAttribute"/> and reports any missing or outdated 
    /// generated types using the given <see cref="NamingConvention"/> to locate the
    /// generated types.
    /// </summary>
    // TODO: F#
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public class StuntGeneratorAnalyzer : DiagnosticAnalyzer
    {
        static readonly DiagnosticDescriptor missing = new DiagnosticDescriptor(
            "ST001",
            new ResourceString(nameof(Resources.MissingStuntAnalyzer_Title)),
            new ResourceString(nameof(Resources.MissingStuntAnalyzer_Message)), 
            "Build", 
            DiagnosticSeverity.Error, 
            true,
            new ResourceString(nameof(Resources.MissingStuntAnalyzer_Description)));

        static readonly DiagnosticDescriptor outdated = new DiagnosticDescriptor(
            "ST002",
            new ResourceString(nameof(Resources.OutdatedStuntAnalyzer_Title)),
            new ResourceString(nameof(Resources.OutdatedStuntAnalyzer_Message)),
            "Build",
            DiagnosticSeverity.Error,
            true,
            new ResourceString(nameof(Resources.OutdatedStuntAnalyzer_Description)));

        static readonly HashSet<string> outdatedDiagnosticIds = new HashSet<string>
        {
            // C# non-implemented abstract member
            "CS0534",
            // C# non-implemented interface member
            "CS0535",
            // VB non-implemented abstract member
            "BC30610",
            // VB non-implemented interface member
            "BC30149",
        };

        readonly NamingConvention naming;
        Type generatorAttribute;

        /// <summary>
        /// Instantiates the analyzer with the default <see cref="NamingConvention"/> and 
        /// for method invocations annotated with <see cref="StuntGeneratorAttribute"/>.
        /// </summary>
        public StuntGeneratorAnalyzer() : this(new NamingConvention(), typeof(StuntGeneratorAttribute)) { }

        /// <summary>
        /// Customizes the analyzer by specifying a custom <see cref="NamingConvention"/> and 
        /// <see cref="generatorAttribute"/> to lookup in method invocations.
        /// </summary>
        protected StuntGeneratorAnalyzer(NamingConvention naming, Type generatorAttribute)
        {
            this.naming = naming;
            this.generatorAttribute = generatorAttribute;
        }

        /// <summary>
        /// Provides metadata about the missing generated type diagnostic.
        /// </summary>
        public virtual DiagnosticDescriptor MissingDescriptor => missing;

        /// <summary>
        /// Provides metadata about the outdated generated type diagnostic.
        /// </summary>
        public virtual DiagnosticDescriptor OutdatedDescriptor => outdated;

        /// <summary>
        /// Returns the single <see cref="MissingDescriptor"/> this analyer supports.
        /// </summary>
        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ImmutableArray.Create(MissingDescriptor, OutdatedDescriptor); }
        }

        /// <summary>
        /// Registers the analyzer to take action on method invocation expressions.
        /// </summary>
        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSyntaxNodeAction(AnalyzeSyntaxNode, Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeSyntaxNode, Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.SimpleMemberAccessExpression);
        }

        void AnalyzeSyntaxNode(SyntaxNodeAnalysisContext context)
        {
            // Get the matching symbol for the given generator attribute from the current compilation.
            var generator = context.Compilation.GetTypeByMetadataName(generatorAttribute.FullName);
            if (generator == null)
                // This may be an extender authoring error, but another analyzer should ensure proper 
                // metadata references exist. Typically, the same nuget package that adds this analyzer 
                // also adds the required assembly references, so this should never happen anyway.
                return;

            var symbol = context.Compilation.GetSemanticModel(context.Node.SyntaxTree).GetSymbolInfo(context.Node);
            if (symbol.Symbol?.Kind == SymbolKind.Method)
            {
                var method = (IMethodSymbol)symbol.Symbol;
                if (method.GetAttributes().Any(x => x.AttributeClass == generator) && 
                    // We don't generate anything if generator is applied to a non-generic method.
                    !method.TypeArguments.IsDefaultOrEmpty)
                {
                    var name = naming.GetFullName(method.TypeArguments.OfType<INamedTypeSymbol>());

                    // See if the stunt already exists
                    var stunt = context.Compilation.GetTypeByMetadataName(name);
                    if (stunt == null)
                    {
                        var diagnostic = Diagnostic.Create(
                            MissingDescriptor,
                            context.Node.GetLocation(),
                            name);

                        context.ReportDiagnostic(diagnostic);
                    }
                    else
                    {
                        // See if the symbol has any compilation error diagnostics associated
                        var diag = context.Compilation.GetDiagnostics()
                            .Where(d => outdatedDiagnosticIds.Contains(d.Id)).ToArray();

                        if (HasDiagnostic(stunt, diag))
                        {
                            // If there are compilation errors, we should update the proxy.
                            var diagnostic = Diagnostic.Create(
                                OutdatedDescriptor,
                                context.Node.GetLocation(),
                                name);

                            context.ReportDiagnostic(diagnostic);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks if any of the diagnostics provided applies to the given symbol.
        /// </summary>
        bool HasDiagnostic(INamedTypeSymbol symbol, Diagnostic[] diagnostics)
        {
            var symbolPath = symbol.Locations[0].GetLineSpan().Path;
            bool isSymbolLoc(Location loc) => loc.IsInSource && loc.GetLineSpan().Path == symbolPath;

            return diagnostics
                .Any(d => isSymbolLoc(d.Location) || d.AdditionalLocations.Any(isSymbolLoc));
        }
    }
}
