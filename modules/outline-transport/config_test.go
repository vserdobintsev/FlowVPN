// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
)

func testConfig() Config {
	return Config{protocolVersion, "127.0.0.1", 12345, "chacha20-ietf-poly1305", "synthetic test password", base64.StdEncoding.EncodeToString([]byte("POST ")), 1, "test-user", "test-password"}
}

func TestConfigBoundaries(t *testing.T) {
	tests := map[string]func(*Config){
		"protocol":           func(c *Config) { c.ProtocolVersion = 2 },
		"hostname":           func(c *Config) { c.Server = "must-not-resolve.invalid" },
		"url":                func(c *Config) { c.Server = "https://127.0.0.1" },
		"unspecified":        func(c *Config) { c.Server = "0.0.0.0" },
		"multicast":          func(c *Config) { c.Server = "224.0.0.1" },
		"zone":               func(c *Config) { c.Server = "fe80::1%1" },
		"mapped-ipv4":        func(c *Config) { c.Server = "::ffff:127.0.0.1" },
		"port-zero":          func(c *Config) { c.ServerPort = 0 },
		"port-large":         func(c *Config) { c.ServerPort = 65536 },
		"method":             func(c *Config) { c.Method = "2022-blake3-aes-128-gcm" },
		"interface-zero":     func(c *Config) { c.InterfaceIndex = 0 },
		"interface-large":    func(c *Config) { c.InterfaceIndex = 0x1000000 },
		"username-empty":     func(c *Config) { c.SocksUsername = "" },
		"password-empty":     func(c *Config) { c.SocksPassword = "" },
		"credential-control": func(c *Config) { c.SocksPassword = "wrong\nvalue" },
		"credential-large":   func(c *Config) { c.SocksPassword = strings.Repeat("x", 129) },
		"prefix-base64":      func(c *Config) { c.PrefixBase64 = "@@@" },
		"prefix-large":       func(c *Config) { c.PrefixBase64 = base64.StdEncoding.EncodeToString(make([]byte, 17)) },
		"aes128-randomness": func(c *Config) {
			c.Method = "aes-128-gcm"
			c.PrefixBase64 = base64.StdEncoding.EncodeToString(make([]byte, 9))
		},
	}
	for name, mutate := range tests {
		t.Run(name, func(t *testing.T) {
			c := testConfig()
			mutate(&c)
			data, _ := json.Marshal(c)
			if _, err := parseConfig(data); err == nil {
				t.Fatal("invalid config accepted")
			}
		})
	}
	c := testConfig()
	c.Password = "\x00\r\n:@/\"пароль"
	data, _ := json.Marshal(c)
	parsed, err := parseConfig(data)
	if err != nil || parsed.Password != c.Password {
		t.Fatal("opaque password changed")
	}
	for _, method := range []string{"aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305"} {
		c := testConfig()
		c.Method = method
		limit := 16
		if method == "aes-128-gcm" {
			limit = 8
		}
		c.PrefixBase64 = base64.StdEncoding.EncodeToString(make([]byte, limit))
		if _, _, err := c.validate(); err != nil {
			t.Fatalf("valid boundary %s rejected", method)
		}
	}
}

func TestConfigRejectsAmbiguousJSON(t *testing.T) {
	data, _ := json.Marshal(testConfig())
	raw := string(data)
	for name, value := range map[string]string{
		"duplicate":    strings.Replace(raw, "{", "{\"protocolVersion\":1,", 1),
		"case":         strings.Replace(raw, "protocolVersion", "ProtocolVersion", 1),
		"missing":      strings.Replace(raw, "\"protocolVersion\":1,", "", 1),
		"unknown":      strings.Replace(raw, "{", "{\"unexpected\":1,", 1),
		"null":         strings.Replace(raw, "\"password\":\"synthetic test password\"", "\"password\":null", 1),
		"trailing":     raw + "{}",
		"oversized":    strings.Repeat(" ", maxConfigBytes) + raw,
		"invalid-utf8": strings.Replace(raw, "test-user", "\xff", 1),
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := parseConfig([]byte(value)); err == nil {
				t.Fatal("ambiguous JSON accepted")
			}
		})
	}
}
