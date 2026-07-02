namespace CAP_Core.Export;

/// <summary>
/// Guard for exported Python script names. Python puts the script's own directory
/// first on <c>sys.path</c>, so a script named like a module it (transitively)
/// imports — e.g. <c>re.py</c> — shadows that module and fails with a confusing
/// circular-import error regardless of the selected interpreter.
/// </summary>
public static class PythonModuleShadowing
{
    private static readonly HashSet<string> ShadowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Python standard library (the parts realistically imported during a Nazca run)
        "abc", "argparse", "array", "ast", "asyncio", "base64", "bisect", "builtins",
        "calendar", "cmath", "code", "codecs", "collections", "concurrent", "contextlib",
        "copy", "csv", "ctypes", "dataclasses", "datetime", "decimal", "difflib", "dis",
        "email", "enum", "errno", "faulthandler", "fnmatch", "fractions", "functools",
        "gc", "getopt", "getpass", "gettext", "glob", "gzip", "hashlib", "heapq", "hmac",
        "html", "http", "importlib", "inspect", "io", "ipaddress", "itertools", "json",
        "keyword", "linecache", "locale", "logging", "lzma", "marshal", "math",
        "mimetypes", "multiprocessing", "numbers", "opcode", "operator", "os", "pathlib",
        "pickle", "platform", "pprint", "profile", "queue", "random", "re", "reprlib",
        "runpy", "sched", "secrets", "select", "selectors", "shlex", "shutil", "signal",
        "site", "socket", "sqlite3", "ssl", "stat", "statistics", "string", "struct",
        "subprocess", "sys", "sysconfig", "tarfile", "tempfile", "textwrap", "threading",
        "time", "token", "tokenize", "traceback", "types", "typing", "unicodedata",
        "unittest", "urllib", "uuid", "venv", "warnings", "weakref", "webbrowser",
        "xml", "zipfile", "zlib", "zoneinfo",
        // Nazca and its dependency chain
        "nazca", "numpy", "pandas", "scipy", "matplotlib", "pyclipper", "PIL", "pytz",
        "dateutil", "six", "packaging", "pyparsing", "cycler", "kiwisolver", "fontTools",
        "setuptools", "pip",
    };

    /// <summary>
    /// True when a script saved as <paramref name="fileStem"/><c>.py</c> would shadow a
    /// Python module that the Nazca run needs (case-insensitive — Windows file systems are).
    /// </summary>
    /// <param name="fileStem">File name without extension.</param>
    public static bool ShadowsPythonModule(string fileStem) =>
        ShadowedNames.Contains(fileStem);
}
