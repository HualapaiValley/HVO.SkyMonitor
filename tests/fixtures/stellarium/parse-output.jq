[inputs | select(length > 0) | split("|")] as $rows |
($rows[] | select(.[0] == "SETTINGS")) as $s |
{
  settings: {
    width: ($s[1] | tonumber),
    height: ($s[2] | tonumber),
    projection: $s[3],
    fieldOfViewDegrees: ($s[4] | tonumber),
    utc: $s[5],
    mount: $s[6],
    diskViewport: ($s[7] == "true"),
    horizontalFlip: ($s[8] == "true"),
    verticalFlip: ($s[9] == "true"),
    longitudeDegrees: ($s[10] | tonumber),
    latitudeDegrees: ($s[11] | tonumber),
    elevationMeters: ($s[12] | tonumber),
    magnitudeLimit: ($s[13] | tonumber)
  },
  landmarks: [$rows[] | select(.[0] == "LANDMARK") | {
    id: .[1],
    altitudeDegrees: (.[2] | tonumber),
    azimuthDegrees: (.[3] | tonumber),
    native: {x: (.[4] | tonumber), y: (.[5] | tonumber)}
  }],
  stars: [$rows[] | select(.[0] == "STAR") | {
    id: .[1],
    altitudeDegrees: (.[2] | tonumber),
    azimuthDegrees: (.[3] | tonumber),
    native: {x: (.[4] | tonumber), y: (.[5] | tonumber)},
    j2000: {rightAscensionDegrees: (.[6] | tonumber), declinationDegrees: (.[7] | tonumber)}
  }],
  endpoints: [$rows[] | select(.[0] == "ENDPOINT") | {
    id: .[1],
    altitudeDegrees: (.[2] | tonumber),
    azimuthDegrees: (.[3] | tonumber),
    native: {x: (.[4] | tonumber), y: (.[5] | tonumber)},
    j2000: {rightAscensionDegrees: (.[6] | tonumber), declinationDegrees: (.[7] | tonumber)}
  }]
}
