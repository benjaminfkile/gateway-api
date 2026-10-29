using System.Reflection;

namespace Gateway.Api.Tests;

/// <summary>
/// A scripted stand-in for an AWS SDK client interface (<c>IAmazonAutoScaling</c>,
/// <c>IAmazonEC2</c> and the rest), built with <see cref="DispatchProxy"/> so a test
/// fakes only the operations it uses. Every call is recorded with its request; a
/// handler keyed by method name answers it (the returned object becomes the task's
/// result, a thrown exception fails the task). An unscripted call throws
/// <see cref="NotSupportedException"/>. No network is ever reached.
/// </summary>
public class AwsClientProxy<T> : DispatchProxy
    where T : class
{
    private Dictionary<string, Func<object?, object?>> _handlers = new(StringComparer.Ordinal);
    private List<(string Method, object? Request)> _calls = new();

    /// <summary>The calls made, in order, as (method name, request).</summary>
    public IReadOnlyList<(string Method, object? Request)> Calls => _calls;

    /// <summary>Create the client and the proxy that scripts it.</summary>
    public static (T Client, AwsClientProxy<T> Proxy) Create()
    {
        var client = Create<T, AwsClientProxy<T>>();
        return (client, (AwsClientProxy<T>)(object)client);
    }

    /// <summary>Answer <paramref name="method"/> (e.g. <c>DescribeAutoScalingGroupsAsync</c>) with <paramref name="handler"/>.</summary>
    public AwsClientProxy<T> On(string method, Func<object?, object?> handler)
    {
        _handlers[method] = handler;
        return this;
    }

    /// <summary>The requests of every call to <paramref name="method"/>, cast to <typeparamref name="TRequest"/>.</summary>
    public IReadOnlyList<TRequest> Requests<TRequest>(string method) =>
        _calls.Where(c => c.Method == method).Select(c => (TRequest)c.Request!).ToList();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        if (method.Name == "Dispose")
        {
            return null;
        }

        var request = args is { Length: > 0 } ? args[0] : null;
        _calls.Add((method.Name, request));

        var resultType = method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? method.ReturnType.GetGenericArguments()[0]
            : null;

        if (!_handlers.TryGetValue(method.Name, out var handler))
        {
            throw new NotSupportedException($"Unscripted AWS call {method.Name}");
        }

        try
        {
            var result = handler(request);
            if (resultType is null)
            {
                return Task.CompletedTask;
            }

            result ??= Activator.CreateInstance(resultType);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, new[] { result });
        }
        catch (Exception ex)
        {
            if (resultType is null)
            {
                return Task.FromException(ex);
            }

            return typeof(Task).GetMethods()
                .Single(m => m.Name == nameof(Task.FromException) && m.IsGenericMethodDefinition)
                .MakeGenericMethod(resultType)
                .Invoke(null, new object[] { ex });
        }
    }
}
