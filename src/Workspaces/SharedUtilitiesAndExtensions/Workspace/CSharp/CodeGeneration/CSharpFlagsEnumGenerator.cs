﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CodeGeneration;
using Microsoft.CodeAnalysis.Editing;

namespace Microsoft.CodeAnalysis.CSharp.CodeGeneration;

internal sealed class CSharpFlagsEnumGenerator : AbstractFlagsEnumGenerator
{
    public static readonly CSharpFlagsEnumGenerator Instance = new();

    private CSharpFlagsEnumGenerator()
    {
    }

    protected override SyntaxGeneratorInternal SyntaxGenerator
        => CSharpSyntaxGeneratorInternal.Instance;

    protected override SyntaxNode CreateExplicitlyCastedLiteralValue(
        INamedTypeSymbol enumType,
        SpecialType underlyingSpecialType,
        object constantValue)
    {
        var expression = ExpressionGenerator.GenerateNonEnumValueExpression(
            enumType.EnumUnderlyingType, constantValue, canUseFieldReference: true);

        var constantValueULong = underlyingSpecialType.ConvertUnderlyingValueToUInt64(constantValue);
        if (constantValueULong == 0)
        {
            // 0 is always convertible to an enum type without needing a cast.
            return expression;
        }

        return this.SyntaxGenerator.CastExpression(enumType, expression);
    }

    protected override bool IsValidName(INamedTypeSymbol enumType, string name)
        => SyntaxFacts.IsValidIdentifier(name);
}
