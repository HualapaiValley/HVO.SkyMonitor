namespace HVO.SkyMonitor.Astronomy;

/// <summary>A unit-capable Cartesian vector in the local east, north, up frame.</summary>
public readonly record struct EnuVector(double East, double North, double Up)
{
    /// <summary>Gets the Euclidean length of the vector.</summary>
    public double Length => Math.Sqrt(East * East + North * North + Up * Up);

    /// <summary>Returns this vector normalized to unit length.</summary>
    public EnuVector Normalize()
    {
        var length = Length;
        if (!double.IsFinite(length) || length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EnuVector), "A finite non-zero vector is required.");
        }

        return this / length;
    }

    /// <summary>Returns the scalar product of two ENU vectors.</summary>
    public static double Dot(EnuVector left, EnuVector right)
        => left.East * right.East + left.North * right.North + left.Up * right.Up;

    /// <summary>Returns the right-handed vector product of two ENU vectors.</summary>
    public static EnuVector Cross(EnuVector left, EnuVector right)
        => new(
            left.North * right.Up - left.Up * right.North,
            left.Up * right.East - left.East * right.Up,
            left.East * right.North - left.North * right.East);

    /// <summary>Adds two vectors.</summary>
    public static EnuVector operator +(EnuVector left, EnuVector right)
        => Add(left, right);

    /// <summary>Adds two vectors.</summary>
    public static EnuVector Add(EnuVector left, EnuVector right)
        => new(left.East + right.East, left.North + right.North, left.Up + right.Up);

    /// <summary>Subtracts two vectors.</summary>
    public static EnuVector operator -(EnuVector left, EnuVector right)
        => Subtract(left, right);

    /// <summary>Subtracts one vector from another.</summary>
    public static EnuVector Subtract(EnuVector left, EnuVector right)
        => new(left.East - right.East, left.North - right.North, left.Up - right.Up);

    /// <summary>Scales a vector.</summary>
    public static EnuVector operator *(EnuVector value, double scale)
        => Multiply(value, scale);

    /// <summary>Scales a vector.</summary>
    public static EnuVector Multiply(EnuVector value, double scale)
        => new(value.East * scale, value.North * scale, value.Up * scale);

    /// <summary>Divides a vector by a scalar.</summary>
    public static EnuVector operator /(EnuVector value, double scale)
        => Divide(value, scale);

    /// <summary>Divides a vector by a scalar.</summary>
    public static EnuVector Divide(EnuVector value, double scale)
        => new(value.East / scale, value.North / scale, value.Up / scale);
}

/// <summary>An immutable orthonormal camera frame expressed in local ENU coordinates.</summary>
public readonly record struct CameraBasis(EnuVector Right, EnuVector ImageUp, EnuVector Boresight)
{
    /// <summary>
    /// Creates a camera frame. Zero roll places local vertical toward image-up;
    /// at zenith its continuous limit is north-up. Positive roll is right-handed
    /// about boresight. Horizontal flip reverses camera-right.
    /// </summary>
    public static CameraBasis Create(
        double boresightAltitudeDegrees,
        double boresightAzimuthDegrees,
        double rollDegrees = 0,
        bool horizontalFlip = false)
    {
        ValidateHorizontal(boresightAltitudeDegrees, boresightAzimuthDegrees);
        if (!double.IsFinite(rollDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(rollDegrees));
        }

        var forward = FromHorizontal(new AltAzPoint(boresightAltitudeDegrees, boresightAzimuthDegrees));
        var worldUp = new EnuVector(0, 0, 1);
        var imageUp = worldUp - forward * EnuVector.Dot(worldUp, forward);
        if (imageUp.Length < 1e-12)
        {
            imageUp = new EnuVector(0, 1, 0);
        }
        else
        {
            imageUp = imageUp.Normalize();
        }

        var right = EnuVector.Cross(imageUp, forward).Normalize();
        var roll = DegreesToRadians(rollDegrees);
        var rolledRight = right * Math.Cos(roll) + imageUp * Math.Sin(roll);
        var rolledUp = imageUp * Math.Cos(roll) - right * Math.Sin(roll);
        if (horizontalFlip)
        {
            rolledRight *= -1;
        }

        return new CameraBasis(rolledRight, rolledUp, forward);
    }

    /// <summary>Converts horizontal coordinates to a unit ENU direction.</summary>
    public static EnuVector FromHorizontal(AltAzPoint direction)
    {
        ValidateHorizontal(direction.AltitudeDegrees, direction.AzimuthDegrees);
        var altitude = DegreesToRadians(direction.AltitudeDegrees);
        var azimuth = DegreesToRadians(NormalizeDegrees(direction.AzimuthDegrees));
        var horizontal = Math.Cos(altitude);
        return new EnuVector(
            horizontal * Math.Sin(azimuth),
            horizontal * Math.Cos(azimuth),
            Math.Sin(altitude));
    }

    /// <summary>Converts a non-zero ENU direction to horizontal coordinates.</summary>
    public static AltAzPoint ToHorizontal(EnuVector direction)
    {
        var unit = direction.Normalize();
        var altitude = RadiansToDegrees(Math.Asin(Math.Clamp(unit.Up, -1d, 1d)));
        var azimuth = NormalizeDegrees(RadiansToDegrees(Math.Atan2(unit.East, unit.North)));
        return new AltAzPoint(altitude, azimuth);
    }

    /// <summary>Expresses an ENU direction as camera-right, camera-up, and boresight components.</summary>
    public EnuVector ToCamera(EnuVector direction)
    {
        var unit = direction.Normalize();
        return new EnuVector(
            EnuVector.Dot(unit, Right),
            EnuVector.Dot(unit, ImageUp),
            EnuVector.Dot(unit, Boresight));
    }

    /// <summary>Converts camera-right, camera-up, and boresight components to ENU.</summary>
    public EnuVector ToEnu(EnuVector cameraDirection)
    {
        var unit = cameraDirection.Normalize();
        return (Right * unit.East + ImageUp * unit.North + Boresight * unit.Up).Normalize();
    }

    private static void ValidateHorizontal(double altitudeDegrees, double azimuthDegrees)
    {
        if (!double.IsFinite(altitudeDegrees) || altitudeDegrees is < -90 or > 90 || !double.IsFinite(azimuthDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(altitudeDegrees));
        }
    }

    private static double NormalizeDegrees(double value) => ((value % 360d) + 360d) % 360d;
    private static double DegreesToRadians(double value) => value * Math.PI / 180d;
    private static double RadiansToDegrees(double value) => value * 180d / Math.PI;
}
