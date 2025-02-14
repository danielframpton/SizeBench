﻿using System.ComponentModel.DataAnnotations;

namespace SizeBench.AnalysisEngine.Symbols;

public interface ISymbol
{
    string Name { get; }

    [Display(AutoGenerateField = false)]
    public SymbolComparisonClass SymbolComparisonClass { get; }

    [DisplayFormat(DataFormatString = "0x{0:X}")]
    uint RVA { get; }

    [DisplayFormat(DataFormatString = "0x{0:X}")]
    uint RVAEnd { get; }

    [Display(Name = "Size on disk")]
    uint Size { get; }

    [Display(Name = "Size in memory")]
    uint VirtualSize { get; }

    [Display(AutoGenerateField = false)]
    bool IsCOMDATFolded { get; }

    // Whether two symbols are considered "the same" in a diff is a difficult question to answer.  If they have the same name, they're
    // probably the same, unless one is volatile and the other is not, or in the case of functions if they have differing parameters, or...
    // you see where this is going. It's tricky.  So each symbol type has to decide if something is "probably the same as me."
    bool IsVeryLikelyTheSameAs(ISymbol otherSymbol);
}
