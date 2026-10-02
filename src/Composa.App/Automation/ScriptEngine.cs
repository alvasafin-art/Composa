using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using Jint;
using Jint.Native;
using Jint.Runtime.Interop;

namespace Composa.App.Automation;

/// <summary>Jint resumes promises on a worker; editor delegates must remain on their owning UI thread.</summary>
internal sealed class ScriptEngine(Engine engine, ScriptMemoryBudget? memory = null)
{
    private readonly bool onUi = Dispatcher.UIThread.CheckAccess();
    public Engine Raw => engine;
    public void SetValue(string name, Delegate callback)
    {
        var parameters = callback.GetType().GetMethod("Invoke")!.GetParameters();
        engine.SetValue(name, new ClrFunction(engine, name, (_, arguments) =>
        {
            var values = parameters.Select((parameter, index) =>
                engine.TypeConverter.Convert(index < arguments.Length ? arguments[index].ToObject() : null, parameter.ParameterType, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return JsValue.FromObject(engine, Invoke(callback, values));
        }));
    }
    public object? Invoke(Delegate callback, object?[] arguments)
    {
        object? Run()
        {
            try { return callback.DynamicInvoke(arguments); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); return null; }
        }
        object? Dispatch() => onUi && !Dispatcher.UIThread.CheckAccess() ? Dispatcher.UIThread.InvokeAsync(Run).GetAwaiter().GetResult() : Run();
        return memory == null ? Dispatch() : memory.Native(Dispatch);
    }
}
