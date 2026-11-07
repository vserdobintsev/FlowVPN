// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"net/netip"
	"unicode/utf8"

	"golang.getoutline.org/sdk/transport/shadowsocks"
)

const (
	moduleVersion   = "1.0.0"
	protocolVersion = 1
	maxConfigBytes  = 1024 * 1024
)

var errConfig = errors.New("invalid adapter configuration")

// Config is a closed IPC contract, not a generic SDK or sing-box configuration.
// PrefixBase64 applies only to TCP, matching Outline's legacy transport semantics.
type Config struct {
	ProtocolVersion int    `json:"protocolVersion"`
	Server          string `json:"server"`
	ServerPort      int    `json:"serverPort"`
	Method          string `json:"method"`
	Password        string `json:"password"`
	PrefixBase64    string `json:"prefixBase64"`
	InterfaceIndex  int    `json:"interfaceIndex"`
	SocksUsername   string `json:"socksUsername"`
	SocksPassword   string `json:"socksPassword"`
}

func parseConfig(data []byte) (Config, error) {
	var config Config
	if len(data) == 0 || len(data) > maxConfigBytes || !utf8.Valid(data) {
		return config, errConfig
	}
	// encoding/json accepts duplicate and case-insensitive fields by default. Reject
	// both before decoding so C# and Go cannot interpret the same message differently.
	allowed := map[string]bool{"protocolVersion": false, "server": false, "serverPort": false, "method": false, "password": false, "prefixBase64": false, "interfaceIndex": false, "socksUsername": false, "socksPassword": false}
	decoder := json.NewDecoder(bytes.NewReader(data))
	first, err := decoder.Token()
	if err != nil || first != json.Delim('{') {
		return config, errConfig
	}
	for decoder.More() {
		token, err := decoder.Token()
		name, ok := token.(string)
		seen, known := allowed[name]
		if err != nil || !ok || !known || seen {
			return config, errConfig
		}
		allowed[name] = true
		var value json.RawMessage
		if decoder.Decode(&value) != nil || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return config, errConfig
		}
	}
	if last, err := decoder.Token(); err != nil || last != json.Delim('}') {
		return config, errConfig
	}
	if _, err := decoder.Token(); err != io.EOF {
		return config, errConfig
	}
	for _, seen := range allowed {
		if !seen {
			return config, errConfig
		}
	}
	if json.Unmarshal(data, &config) != nil {
		return config, errConfig
	}
	if _, _, err := config.validate(); err != nil {
		return Config{}, err
	}
	return config, nil
}

func (c Config) validate() (*shadowsocks.EncryptionKey, []byte, error) {
	address, err := netip.ParseAddr(c.Server)
	if err != nil || address.Zone() != "" || address.Is4In6() || address.IsUnspecified() || address.IsMulticast() || c.ServerPort < 1 || c.ServerPort > 65535 ||
		c.ProtocolVersion != protocolVersion || c.InterfaceIndex < 1 || c.InterfaceIndex > 0xFFFFFF || len(c.Password) > 256*1024 ||
		!credential(c.SocksUsername) || !credential(c.SocksPassword) {
		return nil, nil, errConfig
	}
	if c.Method != "aes-128-gcm" && c.Method != "aes-256-gcm" && c.Method != "chacha20-ietf-poly1305" {
		return nil, nil, errConfig
	}
	key, err := shadowsocks.NewEncryptionKey(c.Method, c.Password)
	if err != nil {
		return nil, nil, errConfig
	}
	if len(c.PrefixBase64) > 24 {
		return nil, nil, errConfig
	}
	prefix, err := base64.StdEncoding.Strict().DecodeString(c.PrefixBase64)
	if err != nil || base64.StdEncoding.EncodeToString(prefix) != c.PrefixBase64 || len(prefix) > min(16, key.SaltSize()-8) {
		return nil, nil, errConfig
	}
	return key, prefix, nil
}

func credential(value string) bool {
	if len(value) == 0 || len(value) > 128 {
		return false
	}
	for _, b := range []byte(value) {
		if b < 33 || b > 126 {
			return false
		}
	}
	return true
}
