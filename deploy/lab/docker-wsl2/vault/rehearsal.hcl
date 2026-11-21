ui = false
disable_mlock = true
api_addr = "https://vault:8200"
cluster_addr = "https://vault:8201"

storage "raft" {
  path    = "/vault/file"
  node_id = "lab-d03"
}

listener "tcp" {
  address         = "0.0.0.0:8200"
  cluster_address = "0.0.0.0:8201"
  tls_min_version = "tls12"
  tls_cert_file   = "/vault/config/leaf.pem"
  tls_key_file    = "/vault/config/leaf.key"
}
