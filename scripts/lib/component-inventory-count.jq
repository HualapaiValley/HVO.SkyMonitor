# The component count the release script reports, using the same definition as component-inventory.jq.
def s: if type == "string" then . else "" end;
def entries: if type == "array" then .[] else empty end;
def is_subject: ([ .annotations | entries | select(type == "object") | .comment | s
                    | select(startswith("ImageID: ")) ] | length) > 0;
if (type == "object") and ((.packages | type) == "array")
then [ .packages[]
       | select(type == "object")
       | select(is_subject | not)
       | select(((.name | s) | gsub("^\\s+|\\s+$"; "")) != "")
       | select(((.versionInfo | s) | gsub("^\\s+|\\s+$"; "")) != "") ] | length
else 0
end
