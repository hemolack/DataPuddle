using DuckDB.NET.Data;
using DuckDB.NET.Data.DataChunk.Reader;
using DuckDB.NET.Data.DataChunk.Writer;

namespace DataPuddle.Plugins;

/// <summary>
/// The only place that talks to DuckDB.NET's scalar-function API. If a DuckDB.NET upgrade changes that
/// API, this is the one file to adjust.
/// </summary>
internal static class DuckFunctionBinder {
    public static void Register(DuckDBConnection connection, PluginFunction function) {
        switch (function.Kind) {
            case PluginValueKind.String:
                switch (function.Arity) {
                    case 1:
                        connection.RegisterScalarFunction<string, string>(function.Name, Wrap<string, string>(function));
                        return;
                    case 2:
                        connection.RegisterScalarFunction<string, string, string>(function.Name, Wrap<string, string>(function));
                        return;
                    case 3:
                        connection.RegisterScalarFunction<string, string, string, string>(function.Name, Wrap<string, string>(function));
                        return;
                }
                break;
            case PluginValueKind.Integer:
                switch (function.Arity) {
                    case 1:
                        connection.RegisterScalarFunction<long, long>(function.Name, Wrap<long, long>(function));
                        return;
                    case 2:
                        connection.RegisterScalarFunction<long, long, long>(function.Name, Wrap<long, long>(function));
                        return;
                }
                break;
            case PluginValueKind.Double:
                switch (function.Arity) {
                    case 1:
                        connection.RegisterScalarFunction<double, double>(function.Name, Wrap<double, double>(function));
                        return;
                    case 2:
                        connection.RegisterScalarFunction<double, double, double>(function.Name, Wrap<double, double>(function));
                        return;
                }
                break;
        }
        throw new NotSupportedException($"No binding for {function.Signature}.");
    }

    // Reads each row's arguments (NULL becomes null), calls the plugin, and writes the result (null becomes NULL).
    private static Action<IReadOnlyList<IDuckDBDataReader>, IDuckDBDataWriter, ulong> Wrap<TIn, TOut>(PluginFunction function)
        where TIn : notnull
        where TOut : notnull {
        return (IReadOnlyList<IDuckDBDataReader> readers, IDuckDBDataWriter writer, ulong rowCount) => {
            object?[] arguments = new object?[function.Arity];
            for (ulong row = 0; row < rowCount; row++) {
                for (int i = 0; i < arguments.Length; i++) {
                    arguments[i] = readers[i].IsValid(row) ? readers[i].GetValue<TIn>(row) : null;
                }

                object? result;
                try {
                    result = function.Invoke(arguments);
                } catch (Exception ex) {
                    throw new InvalidOperationException($"Function {function.Name} (plugin {function.PluginName}) failed: {ex.Message}", ex);
                }

                if (result == null) {
                    writer.WriteNull(row);
                } else {
                    writer.WriteValue((TOut)result, row);
                }
            }
        };
    }
}
