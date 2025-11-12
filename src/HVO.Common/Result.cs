using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using HVO.ComponentModel;

namespace HVO;

/// <summary>
/// Represents a result that can either contain a successful value of type T or an exception.
/// This struct provides a functional approach to error handling without throwing exceptions immediately.
/// </summary>
/// <typeparam name="T">The type of the successful result value</typeparam>
public readonly struct Result<T>
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly T _value;

    private readonly ExceptionDispatchInfo? _exceptionDispatchInfo;

    /// <summary>
    /// Initializes a new successful result with the specified value
    /// </summary>
    /// <param name="value">The successful result value</param>
    public Result(T value) => this._value = value;

    /// <summary>
    /// Initializes a new failed result with the specified exception
    /// </summary>
    /// <param name="error">The exception that caused the failure</param>
    public Result(Exception error) : this(ExceptionDispatchInfo.Capture(error))
    {
    }

    private Result(ExceptionDispatchInfo exceptionDispatchInfo)
    {
        Unsafe.SkipInit(out _value);
        _exceptionDispatchInfo = exceptionDispatchInfo;
    }

    /// <summary>
    /// Gets a value indicating whether this result represents a successful operation
    /// </summary>
    public bool IsSuccessful => _exceptionDispatchInfo is null;

    /// <summary>
    /// Gets a value indicating whether this result represents a failed operation
    /// </summary>
    public bool IsFailure => !IsSuccessful;

    /// <summary>
    /// Gets the successful result value. Throws the captured exception if the result represents a failure.
    /// </summary>
    /// <exception cref="Exception">The exception that caused the failure, if this result is not successful</exception>
    public T Value
    {
        get
        {
            _exceptionDispatchInfo?.Throw(); // No exception set then nothing happens here.
            return _value;
        }
    }

    /// <summary>
    /// Gets the exception that caused the failure, or null if the result is successful
    /// </summary>
    public Exception? Error => _exceptionDispatchInfo?.SourceException;

    /// <summary>
    /// Explicitly converts a Result to its value type
    /// </summary>
    /// <param name="result">The result to convert</param>
    /// <returns>The successful value</returns>
    /// <exception cref="Exception">The exception that caused the failure, if the result is not successful</exception>
    public static explicit operator T(in Result<T> result) => result.Value;

    /// <summary>
    /// Explicitly converts a Result to its error
    /// </summary>
    /// <param name="result">The result to convert</param>
    /// <returns>The exception or null if successful</returns>
    public static explicit operator Exception?(in Result<T> result) => result.Error;

    /// <summary>
    /// Implicitly converts a value to a successful Result
    /// </summary>
    /// <param name="result">The value to wrap</param>
    /// <returns>A successful Result containing the value</returns>
    public static implicit operator Result<T>(T result) => new(result);

    /// <summary>
    /// Implicitly converts an Exception to a failed Result
    /// </summary>
    /// <param name="result">The exception to wrap</param>
    /// <returns>A failed Result containing the exception</returns>
    public static implicit operator Result<T>(Exception result) => new(result);

    /// <summary>
    /// Transforms the result using the provided functions for success and failure cases
    /// </summary>
    /// <typeparam name="R">The return type of the transformation</typeparam>
    /// <param name="success">Function to apply if the result is successful</param>
    /// <param name="failure">Function to apply if the result is a failure</param>
    /// <returns>The transformed result</returns>
    public R Match<R>(Func<T, R> success, Func<Exception?, R> failure) => IsSuccessful ? success(Value) : failure(Error);

    /// <summary>
    /// Creates a successful result containing the specified value
    /// </summary>
    /// <param name="value">The value to wrap</param>
    /// <returns>A successful Result</returns>
    public static Result<T> Success(T value) => new(value);

    /// <summary>
    /// Creates a failed result containing the specified exception
    /// </summary>
    /// <param name="exception">The exception representing the failure</param>
    /// <returns>A failed Result</returns>
    public static Result<T> Failure(Exception exception) => new(exception);
}

/// <summary>
/// Represents a result that can either contain a successful value of type T or an error represented by an enum code and message.
/// This variant allows for more structured error handling with typed error codes.
/// </summary>
/// <typeparam name="T">The type of the successful result value</typeparam>
/// <typeparam name="TEnum">The enum type representing error codes</typeparam>
public readonly struct Result<T, TEnum> where TEnum : Enum
{
    private readonly T _value;
    private readonly (TEnum code, string? message)? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
    }

    private Result(TEnum code, string? message)
    {
        Unsafe.SkipInit(out _value);
        _error = (code, message);
    }

    private Result(TEnum code) : this(code, string.Empty)
    {
        // Set the message to the Description attribute on the Enum value
        _error = (code, code.GetDescription());
    }

    /// <summary>
    /// Gets a value indicating whether this result represents a successful operation
    /// </summary>
    public bool IsSuccessful => _error.HasValue == false;

    /// <summary>
    /// Gets a value indicating whether this result represents a failed operation
    /// </summary>
    public bool IsFailure => !IsSuccessful;

    /// <summary>
    /// Gets the successful result value. Throws InvalidOperationException if the result represents a failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when accessing Value on a failed result</exception>
    public T Value
    {
        get
        {
            if (!IsSuccessful)
                throw new InvalidOperationException($"Cannot access Value on failed result. Error: {Error.Code} - {Error.Message}");
            return _value;
        }
    }

    /// <summary>
    /// Gets the error information including code and message, or default if the result is successful
    /// </summary>
    public (TEnum Code, string? Message) Error => _error.GetValueOrDefault();

    /// <summary>
    /// Implicitly converts a value to a successful Result
    /// </summary>
    /// <param name="value">The value to wrap</param>
    /// <returns>A successful Result containing the value</returns>
    public static implicit operator Result<T, TEnum>(T value) => new Result<T, TEnum>(value);

    /// <summary>
    /// Implicitly converts an error tuple to a failed Result
    /// </summary>
    /// <param name="error">The error code and message</param>
    /// <returns>A failed Result containing the error</returns>
    public static implicit operator Result<T, TEnum>((TEnum Code, string Message) error) =>
        new Result<T, TEnum>(error.Code, error.Message);

    /// <summary>
    /// Implicitly converts an error code to a failed Result (message derived from enum description)
    /// </summary>
    /// <param name="code">The error code</param>
    /// <returns>A failed Result containing the error code and derived message</returns>
    public static implicit operator Result<T, TEnum>(TEnum code) => new Result<T, TEnum>(code);

    /// <summary>
    /// Transforms the result using the provided functions for success and failure cases
    /// </summary>
    /// <typeparam name="R">The return type of the transformation</typeparam>
    /// <param name="success">Function to apply if the result is successful</param>
    /// <param name="failure">Function to apply if the result is a failure</param>
    /// <returns>The transformed result</returns>
    public R Match<R>(Func<T, R> success, Func<(TEnum Code, string? Message), R> failure) => IsSuccessful ? success(Value) : failure(Error);

    /// <summary>
    /// Creates a successful result containing the specified value
    /// </summary>
    /// <param name="value">The value to wrap</param>
    /// <returns>A successful Result</returns>
    public static Result<T, TEnum> Success(T value) => new(value);

    /// <summary>
    /// Creates a failed result with the specified error code and message
    /// </summary>
    /// <param name="code">The error code</param>
    /// <param name="message">The error message</param>
    /// <returns>A failed Result</returns>
    public static Result<T, TEnum> Failure(TEnum code, string? message) => new(code, message);

    /// <summary>
    /// Creates a failed result with the specified error code (message derived from enum description)
    /// </summary>
    /// <param name="code">The error code</param>
    /// <returns>A failed Result</returns>
    public static Result<T, TEnum> Failure(TEnum code) => new(code);
}

