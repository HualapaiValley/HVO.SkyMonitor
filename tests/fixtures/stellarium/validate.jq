def distance($a; $b): ((($a.x-$b.x)*($a.x-$b.x) + ($a.y-$b.y)*($a.y-$b.y)) | sqrt);
def sensor($p): {x: (968 + ($p.x-512)*(595.84/512)), y: (608 + ((1024-$p.y)-512)*(595.84/512))};
def projectedNative($alt; $az):
  ((90-$alt)/90*512) as $r |
  {x: (512 + $r * (($az*3.141592653589793/180) | sin)), y: (512 + $r * (($az*3.141592653589793/180) | cos))};
def projectedSensor($alt; $az): sensor(projectedNative($alt; $az));

($m[0]) as $m | ($a[0]) as $a |
($m.tolerances.astronomyModelDegrees * $m.sensor.imageCircleRadiusPixels / 90) as $astronomyPixels |
{
  fixtureId: $m.fixtureId,
  normalization: $m.sensor.nativeToSensorFormula,
  tolerances: ($m.tolerances + {astronomyModelSensorPixels: $astronomyPixels}),
  settingsChecks: [
    {name:"viewport", passed:($a.settings.width==$m.stellarium.viewportWidth and $a.settings.height==$m.stellarium.viewportHeight), actual:[$a.settings.width,$a.settings.height]},
    {name:"projection", passed:($a.settings.projection==$m.stellarium.projection), actual:$a.settings.projection},
    {name:"fieldOfView", passed:((($a.settings.fieldOfViewDegrees-$m.stellarium.fieldOfViewDegrees)|fabs)<0.01), actual:$a.settings.fieldOfViewDegrees},
    {name:"location", passed:((($a.settings.longitudeDegrees-$m.observer.longitudeDegreesEastPositive)|fabs)<0.0001 and (($a.settings.latitudeDegrees-$m.observer.latitudeDegrees)|fabs)<0.0001 and $a.settings.elevationMeters==$m.observer.elevationMeters), actual:[$a.settings.longitudeDegrees,$a.settings.latitudeDegrees,$a.settings.elevationMeters]},
    {name:"utc", passed:($a.settings.utc|startswith("2025-01-15T08:00:00")), actual:$a.settings.utc},
    {name:"mount", passed:($a.settings.mount==$m.stellarium.mount), actual:$a.settings.mount},
    {name:"diskAndFlips", passed:($a.settings.diskViewport and ($a.settings.horizontalFlip|not) and ($a.settings.verticalFlip|not)), actual:[$a.settings.diskViewport,$a.settings.horizontalFlip,$a.settings.verticalFlip]},
    {name:"magnitudeLimit", passed:((($a.settings.magnitudeLimit-$m.stellarium.magnitudeLimit)|fabs)<0.000001), actual:$a.settings.magnitudeLimit},
    {name:"coordinateRecords", passed:(($a.landmarks|length)==($m.landmarks|length) and ($a.stars|length)==($m.stars|length)), actual:{landmarks:($a.landmarks|length),stars:($a.stars|length)}}
  ],
  analyticChecks: [$m.landmarks[] as $e | ($a.landmarks[] | select(.id==$e.id)) as $v |
    (sensor($v.native)) as $normalized |
    {id:$e.id, native:$v.native, normalizedSensor:$normalized, expectedSensor:$e.expectedSensor,
     errorSensorPixels:distance($normalized;$e.expectedSensor),
     passed:(distance($normalized;$e.expectedSensor) <= $m.tolerances.analyticSensorPixels)}],
  astronomyChecks: [$m.stars[] as $e | ($a.stars[] | select(.id==$e.id)) as $v |
    (projectedSensor($v.altitudeDegrees;$v.azimuthDegrees)) as $fromHorizontal |
    {id:$e.id, name:$e.name, stellariumHorizontal:{altitudeDegrees:$v.altitudeDegrees,azimuthDegrees:$v.azimuthDegrees},
     fixtureExpectedSensor:$e.expectedSensor, sensorFromStellariumHorizontal:$fromHorizontal,
     errorSensorPixels:distance($fromHorizontal;$e.expectedSensor),
     passed:(distance($fromHorizontal;$e.expectedSensor) <= $astronomyPixels)}],
  screenChecks: [$m.stars[] as $e | ($a.stars[] | select(.id==$e.id)) as $v |
    (projectedNative($v.altitudeDegrees;$v.azimuthDegrees)) as $expectedNative |
    (sensor($v.native)) as $normalized |
    {id:$e.id, native:$v.native, expectedNativeFromHorizontal:$expectedNative, normalizedSensor:$normalized,
     errorNativePixels:distance($v.native;$expectedNative),
     passed:(distance($v.native;$expectedNative) <= $m.tolerances.screenRoundingNativePixels)}],
  canonicalSensorChecks: [$m.stars[] as $e | ($a.stars[] | select(.id==$e.id)) as $v |
    (sensor($v.native)) as $normalized |
    (($m.tolerances.screenRoundingNativePixels * $m.sensor.imageCircleRadiusPixels / 512) + $astronomyPixels) as $budget |
    {id:$e.id, native:$v.native, normalizedSensor:$normalized, expectedSensor:$e.expectedSensor,
     astronomyModelSensorPixels:$astronomyPixels,
     screenRoundingSensorPixels:($m.tolerances.screenRoundingNativePixels * $m.sensor.imageCircleRadiusPixels / 512),
     errorSensorPixels:distance($normalized;$e.expectedSensor), toleranceSensorPixels:$budget,
     passed:(distance($normalized;$e.expectedSensor) <= $budget)}]
} |
.passed = (([.settingsChecks[],.analyticChecks[],.astronomyChecks[],.screenChecks[],.canonicalSensorChecks[]] | all(.passed)))
