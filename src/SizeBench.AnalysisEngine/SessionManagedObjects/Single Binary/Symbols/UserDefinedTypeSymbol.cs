﻿using System.Diagnostics;
using SizeBench.AnalysisEngine.DIAInterop;

namespace SizeBench.AnalysisEngine.Symbols;

[DebuggerDisplay("User-Defined Type Symbol Name={Name}")]
public sealed class UserDefinedTypeSymbol : TypeSymbol
{
    #region Base Types and Derived Types

    // Calculating what base types this type has can be somewhat expensive and not everyone who looks at a UDT cares whether the base type
    // info is available.  So it's optional and can be populated on-demand.

    [DebuggerDisplay("Base Type: {_baseTypeSymbol.Name}, Offset = {_offset}")]
    internal readonly struct BaseType
    {
        internal readonly UserDefinedTypeSymbol _baseTypeSymbol;
        internal readonly uint _offset;

        internal BaseType(UserDefinedTypeSymbol baseType, uint offset)
        {
            this._baseTypeSymbol = baseType;
            this._offset = offset;
        }
    }

    private Dictionary<uint, uint>? _baseTypeIDs;

    private bool _areBaseTypesLoaded;

    private List<BaseType>? _baseTypes;
    internal List<BaseType>? BaseTypes
    {
        get
        {
            if (!this._areBaseTypesLoaded)
            {
                throw new InvalidOperationException($"Trying to access {nameof(this.BaseTypes)} but you haven't called {nameof(LoadBaseTypes)} yet!");
            }

            return this._baseTypes;
        }
    }

    // This loads all the base types for this type, and all of their base types and so on for the entire hierarchy up to the root
    internal void LoadBaseTypes(SessionDataCache dataCache, IDIAAdapter diaAdapter, CancellationToken cancellationToken)
    {
        if (this._areBaseTypesLoaded)
        {
            return;
        }

        if (this._baseTypeIDs is null || this._baseTypeIDs.Count == 0)
        {
            this._baseTypeIDs = null; // Allow it to be GC'd if it was somehow an empty collection
            this._areBaseTypesLoaded = true;
            return;
        }

        this._baseTypes = new List<BaseType>(this._baseTypeIDs.Count);

        foreach (var kvp in this._baseTypeIDs)
        {
            if (dataCache.AllTypesBySymIndexId.TryGetValue(kvp.Key, out var baseTypeSymbol))
            {

                // The code hasn't been written yet to handle a base type that's not a UDT - is it possible to have any other type
                // of base type?
                if (baseTypeSymbol is not UserDefinedTypeSymbol baseTypeUDT)
                {
                    throw new InvalidOperationException("Something has gone wrong...");
                }

                baseTypeUDT.LoadBaseTypes(dataCache, diaAdapter, cancellationToken);
                this._baseTypes.Add(new BaseType(baseTypeUDT, kvp.Value));
            }
            else
            {
                var newUDT = diaAdapter.FindTypeSymbolBySymIndexId<UserDefinedTypeSymbol>(kvp.Key, cancellationToken);

                if (newUDT is null)
                {
                    throw new InvalidOperationException("Something went wrong loading a base type...");
                }

                newUDT.LoadBaseTypes(dataCache, diaAdapter, cancellationToken);
                this._baseTypes.Add(new BaseType(newUDT, kvp.Value));
            }
        }
        this._baseTypeIDs = null; // we don't need this anymore, we can let it get GC'd.

        this._areBaseTypesLoaded = true;
    }

    private bool _areDerivedTypesLoaded;
    private SortedList<uint, UserDefinedTypeSymbol>? _derivedTypesBySymIndexId;
    internal SortedList<uint, UserDefinedTypeSymbol>? DerivedTypesBySymIndexId
    {
        get
        {
            if (!this._areDerivedTypesLoaded)
            {
                throw new InvalidOperationException($"Trying to access {nameof(this.DerivedTypesBySymIndexId)} but you haven't yet ensured all derived clasess are loaded!");
            }

            return this._derivedTypesBySymIndexId;
        }
    }

    internal void AddDerivedType(UserDefinedTypeSymbol typeDerivedFromThisOne)
    {
        // If someone tries to call this to add a derived type that we arleady know about, we'll let that slide even
        // if AreDerivedClassesLoaded == true, it makes the calling code simpler to write.
        if (this._derivedTypesBySymIndexId?.ContainsKey(typeDerivedFromThisOne.SymIndexId) == true)
        {
            return;
        }

        if (this._areDerivedTypesLoaded)
        {
            throw new InvalidOperationException("Can't add a derived type after the type has set AreDerivedTypesLoaded==true");
        }

        if (this._derivedTypesBySymIndexId is null)
        {
            this._derivedTypesBySymIndexId = new SortedList<uint, UserDefinedTypeSymbol>(capacity: 5)
                {
                    { typeDerivedFromThisOne.SymIndexId, typeDerivedFromThisOne }
                };
        }
        else if (!this._derivedTypesBySymIndexId.ContainsKey(typeDerivedFromThisOne.SymIndexId))
        {
            this._derivedTypesBySymIndexId.Add(typeDerivedFromThisOne.SymIndexId, typeDerivedFromThisOne);
        }
    }

    // We don't just have a property setter because we only want this to be able to go from false->true, never the other direction.
    internal void MarkDerivedTypesLoaded()
    {
        this._derivedTypesBySymIndexId?.TrimExcess();
        this._areDerivedTypesLoaded = true;
    }

    #endregion

    #region Functions

    // Loading functions is expensive so we defer it until somebody needs it since many callers don't care about all the functions
    // on a UDT.

    private List<IFunctionCodeSymbol>? _functions;

    // This is internal and must only be called on the DIA thread, but it's very convenient for other things already on the DIA thread to be
    // able to get synchronous access to Functions as a property instead of awaiting every time they want to load this.
    internal List<IFunctionCodeSymbol> Functions
    {
        get
        {
            EnsureFunctionsLoaded(CancellationToken.None);

            return this._functions!;
        }
    }

    public async ValueTask<IReadOnlyList<IFunctionCodeSymbol>> GetFunctionsAsync(CancellationToken token)
    {
        if (this._functions is null)
        {
            this._functions = (await this._session.EnumerateFunctionsFromUserDefinedType(this, token).ConfigureAwait(true)).ToList();
        }

        return this._functions;
    }

    internal void EnsureFunctionsLoaded(CancellationToken cancellationToken)
    {
        if (this._functions != null)
        {
            return;
        }

        this._functions = this._diaAdapter.FindAllFunctionsWithinUDT(this.SymIndexId, cancellationToken).ToList();
        this._functions.TrimExcess();
    }

    #endregion

    #region Data Members

    private bool _areDataMembersLoaded;

    private MemberDataSymbol[]? _dataMembers;
    internal MemberDataSymbol[] DataMembers
    {
        get
        {
            EnsureDataMembersLoaded(CancellationToken.None);

            return this._dataMembers!;
        }
    }

    internal void EnsureDataMembersLoaded(CancellationToken cancellationToken)
    {
        if (this._areDataMembersLoaded)
        {
            return;
        }

        this._dataMembers = this._diaAdapter.FindAllMemberDataSymbolsWithinUDT(this, cancellationToken).ToArray();

        this._areDataMembersLoaded = true;
    }

    #endregion

    #region VTableCount

    private bool _isVTableCountLoaded;

    private byte _vtableCount;
    internal byte VTableCount
    {
        get
        {
            EnsureVTableCountLoaded();

            return this._vtableCount;
        }
    }

    internal void EnsureVTableCountLoaded()
    {
        if (this._isVTableCountLoaded)
        {
            return;
        }

        this._vtableCount = this._diaAdapter.FindCountOfVTablesWithin(this.SymIndexId);

        this._isVTableCountLoaded = true;
    }

    #endregion

    internal readonly UserDefinedTypeKind _userDefinedTypeKind;
    private readonly IDIAAdapter _diaAdapter;
    private readonly ISession _session;

    internal UserDefinedTypeSymbol(SessionDataCache dataCache,
                                   IDIAAdapter diaAdapter,
                                   ISession session,
                                   string name,
                                   uint instanceSize,
                                   uint symIndexId,
                                   UserDefinedTypeKind udtKind,
                                   Dictionary<uint, uint>? baseTypeIDs) : base(dataCache, name, instanceSize, symIndexId)
    {
        this._baseTypeIDs = baseTypeIDs;
        this._userDefinedTypeKind = udtKind;
        this._diaAdapter = diaAdapter;
        this._session = session;
    }

    public override bool CanLoadLayout => true;
}

internal static class UserDefinedTypeSymbolExtensions
{
    internal static void LoadAllBaseTypes(this List<UserDefinedTypeSymbol> udts,
                                          SessionDataCache dataCache,
                                          IDIAAdapter diaAdapter,
                                          CancellationToken cancellationToken,
                                          Action<string, uint, uint?> progressReporter)
    {
        const int loggerOutputVelocity = 100;
        uint nextLoggerOutput = loggerOutputVelocity;
        var udtsEnumerated = 0;

        for (var i = 0; i < udts.Count; i++)
        {
            udtsEnumerated++;
            if (udtsEnumerated >= nextLoggerOutput)
            {
                progressReporter($"Base type information loaded for {udtsEnumerated}/{udts.Count} user-defined types so far.", nextLoggerOutput, (uint)udts.Count);
                nextLoggerOutput += loggerOutputVelocity;
            }

            cancellationToken.ThrowIfCancellationRequested();

            udts[i].LoadBaseTypes(dataCache, diaAdapter, cancellationToken);
        }
    }

    internal static void LoadAllDerivedTypes(this List<UserDefinedTypeSymbol> udts,
                                             CancellationToken cancellationToken,
                                             Action<string, uint, uint?> progressReporter)
    {
        const int loggerOutputVelocity = 100;
        uint nextLoggerOutput = loggerOutputVelocity;
        var udtsEnumerated = 0;

        foreach (var udt in udts)
        {
            udtsEnumerated++;
            if (udtsEnumerated >= nextLoggerOutput)
            {
                progressReporter($"Derived type information processed for {udtsEnumerated}/{udts.Count} user-defined types so far.", nextLoggerOutput, (uint)udts.Count);
                nextLoggerOutput += loggerOutputVelocity;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // If this has any base types, then add this as a derived type to all the bases (and their bases, and so on)
            if (udt.BaseTypes != null)
            {
                AddDerivedTypeToBaseTypes(udt, udt.BaseTypes);
            }
        }

        foreach (var udt in udts)
        {
            udt.MarkDerivedTypesLoaded();
        }
    }

    private static void AddDerivedTypeToBaseTypes(UserDefinedTypeSymbol derivedType, List<UserDefinedTypeSymbol.BaseType> baseTypes)
    {
        foreach (var baseType in baseTypes)
        {
            baseType._baseTypeSymbol.AddDerivedType(derivedType);

            if (baseType._baseTypeSymbol.BaseTypes != null)
            {
                AddDerivedTypeToBaseTypes(derivedType, baseType._baseTypeSymbol.BaseTypes);
            }
        }
    }
}
