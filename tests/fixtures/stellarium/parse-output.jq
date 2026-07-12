def boolean:
  if . == "true" then true
  elif . == "false" then false
  else error("invalid boolean token: \(.)")
  end;

[inputs | select(length > 0) | split("|")] as $rows |
($rows | map(select(.[0] == "SETTINGS"))) as $settingsRows |
if ($settingsRows | length) != 1 then error("expected exactly one SETTINGS row")
elif ($rows | map(select(
  (.[0] == "SETTINGS" and length == 14) or
  (.[0] == "LANDMARK" and length == 6) or
  ((.[0] == "STAR" or .[0] == "ENDPOINT") and length == 8) or
  (.[0] == "COMPLETE" and length == 1))) | length) != ($rows | length)
then error("malformed or unknown Stellarium output row")
elif ($rows | map(select(.[0] == "COMPLETE")) | length) != 1
then error("expected exactly one COMPLETE row")
else ($settingsRows[0]) as $s |
{
  settings: {
    width: ($s[1] | tonumber),
    height: ($s[2] | tonumber),
    projection: $s[3],
    fieldOfViewDegrees: ($s[4] | tonumber),
    utc: $s[5],
    mount: $s[6],
    diskViewport: ($s[7] | boolean),
    horizontalFlip: ($s[8] | boolean),
    verticalFlip: ($s[9] | boolean),
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
end
