path "auth/token/create/lab-d03-api" {
  capabilities = ["update"]
}
path "auth/token/create/lab-d03-worker" {
  capabilities = ["update"]
}
path "auth/token/create/lab-d03-operator" {
  capabilities = ["update"]
}

path "auth/token/lookup-self" {
  capabilities = ["read"]
}
