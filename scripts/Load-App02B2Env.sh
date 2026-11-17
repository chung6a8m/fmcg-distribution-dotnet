#!/usr/bin/env bash
# Trusted APP-02B.2 parser. Host .env is DATA, never executable shell.
app02b2_load_env() {
  local file="$1" line key value line_no=0
  [[ -f "$file" && -r "$file" && ! -L "$file" ]] || {
    echo "PREPROD_DENY env missing/unreadable/symlink" >&2; return 2; }
  while IFS= read -r line || [[ -n "$line" ]]; do
    ((line_no+=1))
    [[ -z "$line" || "$line" == \#* ]] && continue
    if [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=(.*)$ ]]; then
      key="${BASH_REMATCH[1]}"
      value="${BASH_REMATCH[2]}"
    else
      echo "PREPROD_DENY invalid env assignment line=$line_no" >&2; return 2
    fi
    case "$key" in
      PREPROD_FQDN|PREPROD_BIND_IP|VAULT_ADDR|TLS_CERT_FILE|TLS_KEY_FILE|VAULT_API_TOKEN_FILE|VAULT_WORKER_TOKEN_FILE) ;;
      *) echo "PREPROD_DENY unexpected env key line=$line_no" >&2; return 2 ;;
    esac
    if [[ -z "$value" || "$value" =~ [[:cntrl:]] || \
       "$value" == *'$('* || "$value" == *$'\x60'* || "$value" == *';'* || \
       "$value" == *'|'* || "$value" == *'&'* || "$value" == *'<'* || \
       "$value" == *'>'* || "$value" == *\"* || "$value" == *"'"* ]]; then
      echo "PREPROD_DENY unsafe env value line=$line_no" >&2; return 2
    fi
    export "$key=$value"
  done < "$file"
}
app02b2_load_env "${1:?environment file path required}" || return $?
unset -f app02b2_load_env
