﻿using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using SizeBench.PathLocators;

namespace SizeBench.GUI.Windows;

public sealed class SelectSingleBinaryAndPDBControlViewModel : INotifyPropertyChanged
{
    private readonly IBinaryLocator[] _allLocators;

    public SelectSingleBinaryAndPDBControlViewModel(IBinaryLocator[] allLocators)
    {
        this._allLocators = allLocators;
    }

    private string _pdbPath = String.Empty;
    public string PDBPath
    {
        get => this._pdbPath;
        set
        {
            this._pdbPath = value;
            RaiseOnPropertyChanged();
            InferBinaryPathFromPDBPathIfPossible();
        }
    }

    private string _binaryPath = String.Empty;
    public string BinaryPath
    {
        get => this._binaryPath;
        set
        {
            this._binaryPath = value;
            RaiseOnPropertyChanged();
        }
    }

    private void InferBinaryPathFromPDBPathIfPossible()
    {
        foreach (var locator in this._allLocators)
        {
            if (locator.TryInferBinaryPathFromPDBPath(this.PDBPath, out var binaryPath) &&
                File.Exists(binaryPath))
            {
                this.BinaryPath = binaryPath;
            }
        }
    }

    #region INPC

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseOnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    #endregion

}
