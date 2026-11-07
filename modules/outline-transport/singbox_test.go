// SPDX-License-Identifier: GPL-3.0-only
//go:build windows

package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

// Explicit opt-in only. The executable must match this repository's engine lock.
// The config has no TUN, DNS, API, routes or real servers: both ends are loopback.
func TestPinnedSingBoxInterop(t *testing.T) {
	core := os.Getenv("FLOWVPN_SING_BOX")
	if core == "" {
		t.Skip("set FLOWVPN_SING_BOX for pinned local integration")
	}
	data, err := os.ReadFile(filepath.Join("..", "..", "src", "FlowVPN.WinUI", "engine.lock.json"))
	if err != nil {
		t.Fatal(err)
	}
	var lock struct{ Files map[string]string }
	if json.Unmarshal(data, &lock) != nil {
		t.Fatal("invalid engine lock")
	}
	file, err := os.Open(core)
	if err != nil {
		t.Fatal(err)
	}
	hash := sha256.New()
	_, err = io.Copy(hash, file)
	file.Close()
	if err != nil {
		t.Fatal(err)
	}
	if !strings.EqualFold(hex.EncodeToString(hash.Sum(nil)), lock.Files["sing-box.exe"]) {
		t.Fatal("sing-box hash mismatch")
	}
	fixture := mockShadowsocks(t, "chacha20-ietf-poly1305")
	adapter := startFixtureAdapter(t, fixture.config)
	reservation, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := reservation.Addr().(*net.TCPAddr).Port
	reservation.Close()
	loopback, err := net.InterfaceByIndex(fixture.config.InterfaceIndex)
	if err != nil {
		t.Fatal(err)
	}
	config := map[string]any{
		"log":       map[string]any{"level": "error", "timestamp": false},
		"inbounds":  []any{map[string]any{"type": "socks", "tag": "local", "listen": "127.0.0.1", "listen_port": port, "users": []any{map[string]any{"username": fixture.config.SocksUsername, "password": fixture.config.SocksPassword}}}},
		"outbounds": []any{map[string]any{"type": "socks", "tag": "adapter", "server": "127.0.0.1", "server_port": adapter.Port(), "version": "5", "username": fixture.config.SocksUsername, "password": fixture.config.SocksPassword, "bind_interface": loopback.Name, "inet4_bind_address": "127.0.0.1"}},
		"route":     map[string]any{"final": "adapter"},
	}
	encoded, _ := json.Marshal(config)
	cmd := exec.Command(core, "run", "-c", "stdin")
	cmd.Stdin = bytes.NewReader(encoded)
	var diagnostic bytes.Buffer
	cmd.Stdout = &diagnostic
	cmd.Stderr = &diagnostic
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	t.Cleanup(func() {
		cmd.Process.Kill()
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			t.Error("sing-box fixture did not stop")
		}
	})
	address := net.JoinHostPort("127.0.0.1", strconv.Itoa(port))
	deadline := time.Now().Add(5 * time.Second)
	for {
		conn, err := net.DialTimeout("tcp4", address, 100*time.Millisecond)
		if err == nil {
			conn.Close()
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("sing-box local listener not ready")
		}
		time.Sleep(20 * time.Millisecond)
	}
	stream, _ := socksClient(t, port, fixture.config, 1, "sing-box.invalid:443")
	payload := []byte("pinned sing-box TCP")
	stream.Write(payload)
	got := make([]byte, len(payload))
	if _, err := io.ReadFull(stream, got); err != nil || !bytes.Equal(got, payload) {
		t.Fatalf("sing-box TCP: %v", err)
	}
	expectObserved(t, fixture, "tcp", "sing-box.invalid:443")
	control, bound := socksClient(t, port, fixture.config, 3, "8.8.8.8:53")
	defer control.Close()
	udpTarget, err := net.ResolveUDPAddr("udp4", bound)
	if err != nil {
		t.Fatal(err)
	}
	udp, err := net.DialUDP("udp4", nil, udpTarget)
	if err != nil {
		t.Fatal(err)
	}
	defer udp.Close()
	udp.SetDeadline(time.Now().Add(5 * time.Second))
	header, _ := encodeAddress("8.8.8.8:53")
	packet := append(append([]byte{0, 0, 0}, header...), []byte("pinned sing-box UDP")...)
	udp.Write(packet)
	buffer := make([]byte, 1024)
	n, err := udp.Read(buffer)
	if err != nil || !bytes.Equal(packet, buffer[:n]) {
		t.Fatalf("sing-box UDP: %v", err)
	}
	expectObserved(t, fixture, "udp", "8.8.8.8:53")
}
