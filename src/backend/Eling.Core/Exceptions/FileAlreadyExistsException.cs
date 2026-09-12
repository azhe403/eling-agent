namespace Eling.Core.Exceptions;

/// <summary><c>file_write</c> with overwrite=false against an existing file.</summary>
public sealed class FileAlreadyExistsException(string path)
    : PathAlreadyExistsException(path);
