def distance($a; $b): ((($a.x-$b.x)*($a.x-$b.x) + ($a.y-$b.y)*($a.y-$b.y)) | sqrt);
def sensor($p): {x: (968 + ($p.x-512)*(595.84/512)), y: (608 + ((1024-$p.y)-512)*(595.84/512))};
def projectedNative($alt; $az):
  ((90-$alt)/90*512) as $r |
  {x: (512 + $r * (($az*3.141592653589793/180) | sin)), y: (512 + $r * (($az*3.141592653589793/180) | cos))};
def projectedSensor($alt; $az): sensor(projectedNative($alt; $az));
def radialDistance($p): distance($p; {x:968,y:608});
def ids($items): [$items[] | .id] | sort;
def normalizeAngle($value): $value - 360 * (($value / 360) | floor);
def angularDifference($a; $b): ((normalizeAngle($a)-normalizeAngle($b))|fabs) as $d | [$d,(360-$d)] | min;
def clipToImageCircle($from; $to; $radius):
  ($to.x-$from.x) as $dx |
  ($to.y-$from.y) as $dy |
  ($from.x-968) as $fx |
  ($from.y-608) as $fy |
  ($dx*$dx+$dy*$dy) as $aa |
  (2*($fx*$dx+$fy*$dy)) as $bb |
  ($fx*$fx+$fy*$fy-$radius*$radius) as $cc |
  ((-$bb + (($bb*$bb-4*$aa*$cc) | sqrt))/(2*$aa)) as $t |
  {x:($from.x+$t*$dx),y:($from.y+$t*$dy)};

($m[0]) as $m | ($a[0]) as $a |
($m.tolerances.astronomyModelDegrees * $m.sensor.imageCircleRadiusPixels / 90) as $astronomyPixels |
($m.constellationValidation.endpoints | map(.id) | sort) as $endpointIds |
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
    {name:"coordinateRecords", passed:(ids($a.landmarks)==ids($m.landmarks) and ids($a.stars)==ids($m.stars) and ids($a.endpoints)==$endpointIds), actual:{landmarks:ids($a.landmarks),stars:ids($a.stars),endpoints:ids($a.endpoints)}}
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
     passed:(distance($normalized;$e.expectedSensor) <= $budget)}],
  endpointChecks: [$m.constellationValidation.endpoints[] as $e | ($a.endpoints[] | select(.id==$e.id)) as $v |
    (projectedNative($v.altitudeDegrees;$v.azimuthDegrees)) as $expectedNative |
    (sensor($v.native)) as $normalized |
    {id:$e.id,native:$v.native,normalizedSensor:$normalized,expectedHvoSensor:$e.expectedHvoSensor,expectedNativeFromHorizontal:$expectedNative,
     j2000:$v.j2000,expectedJ2000:$e.j2000,
     radialSensorPixels:radialDistance($normalized),
     errorNativePixels:distance($v.native;$expectedNative),
     errorHvoSensorPixels:distance($normalized;$e.expectedHvoSensor),
     rightAscensionErrorDegrees:angularDifference($v.j2000.rightAscensionDegrees;$e.j2000.rightAscensionDegrees),
     declinationErrorDegrees:(($v.j2000.declinationDegrees-$e.j2000.declinationDegrees)|fabs),
     passed:(distance($v.native;$expectedNative) <= $m.tolerances.screenRoundingNativePixels and
       distance($normalized;$e.expectedHvoSensor) <= $m.tolerances.endpointSensorPixels and
       angularDifference($v.j2000.rightAscensionDegrees;$e.j2000.rightAscensionDegrees) <= $m.tolerances.endpointJ2000Degrees and
       (($v.j2000.declinationDegrees-$e.j2000.declinationDegrees)|fabs) <= $m.tolerances.endpointJ2000Degrees)}],
  constellationChecks: [$m.constellationValidation.segments[] as $s |
    ($a.endpoints[] | select(.id==$s.from)) as $fromValue |
    ($a.endpoints[] | select(.id==$s.to)) as $toValue |
    (sensor($fromValue.native)) as $from |
    (sensor($toValue.native)) as $to |
    (if $s.expectedClipping=="image-circle" then clipToImageCircle($from;$to;$m.sensor.imageCircleRadiusPixels) else null end) as $clip |
    {id:$s.id,constellationId:$s.constellationId,from:$from,to:$to,expectedClipping:$s.expectedClipping,
     fromRadiusSensorPixels:radialDistance($from),toRadiusSensorPixels:radialDistance($to),
     stellariumChordBoundary:$clip,expectedHvoBoundary:($s.expectedHvoBoundary // null),
     hvoBoundaryComparisonErrorSensorPixels:(if $clip==null then null else distance($clip;$s.expectedHvoBoundary) end),
     passed:(if $s.expectedClipping=="none"
       then radialDistance($from) <= $m.sensor.imageCircleRadiusPixels and radialDistance($to) <= $m.sensor.imageCircleRadiusPixels
       else radialDistance($from) <= $m.sensor.imageCircleRadiusPixels and radialDistance($to) > $m.sensor.imageCircleRadiusPixels and distance($clip;$s.expectedHvoBoundary) <= $m.tolerances.clippedBoundarySensorPixels
       end)}],
  topologyQualification: $m.constellationValidation.stellariumSkyCultureTopology,
  connectivitySource: {name:$m.constellationValidation.connectivitySource,sourceSha256:$m.constellationValidation.connectivitySourceSha256,embeddedSha256:$m.constellationValidation.embeddedTopologySha256}
} |
.passed = (([.settingsChecks[],.analyticChecks[],.astronomyChecks[],.screenChecks[],.canonicalSensorChecks[],.endpointChecks[],.constellationChecks[]] | all(.passed)))
