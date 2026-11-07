// SPDX-License-Identifier: GPL-3.0-only
//go:build windows

package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"io"
	"net"
	"os"
	"os/exec"
	"strconv"
	"sync"
	"testing"
	"time"

	"golang.getoutline.org/sdk/transport/shadowsocks"
)

type observed struct {
	network, address string
	salt             []byte
}
type fixture struct {
	config Config
	seen   chan observed
}

func loopbackIndex(t testing.TB) int {
	t.Helper()
	interfaces, err := net.Interfaces()
	if err != nil {
		t.Fatal(err)
	}
	for _, item := range interfaces {
		if item.Flags&net.FlagLoopback != 0 {
			return item.Index
		}
	}
	t.Fatal("no loopback interface")
	return 0
}

// The fixture decrypts and echoes locally. Destination names are observed but
// never resolved or connected, so tests cannot send provider/external traffic.
func mockShadowsocks(t testing.TB, method string) fixture {
	t.Helper()
	config := testConfig()
	config.Method = method
	config.InterfaceIndex = loopbackIndex(t)
	key, _, err := config.validate()
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	config.ServerPort = listener.Addr().(*net.TCPAddr).Port
	udp, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: config.ServerPort})
	if err != nil {
		listener.Close()
		t.Fatal(err)
	}
	seen := make(chan observed, 64)
	var workers sync.WaitGroup
	var mu sync.Mutex
	clients := map[*net.TCPConn]bool{}
	workers.Add(2)
	go func() {
		defer workers.Done()
		for {
			conn, err := listener.AcceptTCP()
			if err != nil {
				return
			}
			mu.Lock()
			clients[conn] = true
			mu.Unlock()
			workers.Add(1)
			go func() {
				defer workers.Done()
				defer conn.Close()
				defer func() { mu.Lock(); delete(clients, conn); mu.Unlock() }()
				conn.SetDeadline(time.Now().Add(10 * time.Second))
				salt := make([]byte, key.SaltSize())
				if _, err := io.ReadFull(conn, salt); err != nil {
					return
				}
				reader := shadowsocks.NewReader(io.MultiReader(bytes.NewReader(salt), conn), key)
				address, _, err := readAddress(reader)
				if err != nil {
					return
				}
				select {
				case seen <- observed{"tcp", address, salt}:
				default:
				}
				_, _ = io.Copy(shadowsocks.NewWriter(conn, key), reader)
			}()
		}
	}()
	go func() {
		defer workers.Done()
		buffer := make([]byte, 65536)
		for {
			n, from, err := udp.ReadFromUDP(buffer)
			if err != nil {
				return
			}
			salt := append([]byte(nil), buffer[:key.SaltSize()]...)
			plain, err := shadowsocks.Unpack(nil, buffer[:n], key)
			if err != nil {
				continue
			}
			address, _, err := readAddress(bytes.NewReader(plain))
			if err != nil {
				continue
			}
			select {
			case seen <- observed{"udp", address, salt}:
			default:
			}
			packet, err := shadowsocks.Pack(make([]byte, len(plain)+key.SaltSize()+key.TagSize()), plain, key)
			if err == nil {
				udp.WriteToUDP(packet, from)
			}
		}
	}()
	t.Cleanup(func() {
		listener.Close()
		udp.Close()
		mu.Lock()
		for conn := range clients {
			conn.Close()
		}
		mu.Unlock()
		workers.Wait()
	})
	return fixture{config, seen}
}

func startFixtureAdapter(t testing.TB, c Config) *server {
	t.Helper()
	out, err := makeOutbound(c)
	if err != nil {
		t.Fatal(err)
	}
	s, err := newServer(c.SocksUsername, c.SocksPassword, out)
	if err != nil {
		t.Fatal(err)
	}
	go s.Serve()
	t.Cleanup(func() {
		s.Close()
		select {
		case <-s.Done():
		case <-time.After(3 * time.Second):
			t.Error("adapter did not stop")
		}
	})
	return s
}

func socksClient(t testing.TB, port int, c Config, command byte, destination string) (net.Conn, string) {
	t.Helper()
	conn, err := net.DialTimeout("tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)), 3*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	conn.SetDeadline(time.Now().Add(5 * time.Second))
	conn.Write([]byte{5, 1, 2})
	var reply [2]byte
	if _, err = io.ReadFull(conn, reply[:]); err != nil || reply != [2]byte{5, 2} {
		t.Fatalf("method negotiation: %v %v", reply, err)
	}
	auth := append([]byte{1, byte(len(c.SocksUsername))}, c.SocksUsername...)
	auth = append(auth, byte(len(c.SocksPassword)))
	auth = append(auth, c.SocksPassword...)
	conn.Write(auth)
	if _, err = io.ReadFull(conn, reply[:]); err != nil || reply != [2]byte{1, 0} {
		t.Fatalf("authentication: %v %v", reply, err)
	}
	address, err := encodeAddress(destination)
	if err != nil {
		t.Fatal(err)
	}
	conn.Write(append([]byte{5, command, 0}, address...))
	var header [3]byte
	if _, err = io.ReadFull(conn, header[:]); err != nil || header != [3]byte{5, 0, 0} {
		t.Fatalf("command: %v %v", header, err)
	}
	bound, _, err := readAddress(conn)
	if err != nil {
		t.Fatal(err)
	}
	return conn, bound
}

func expectObserved(t testing.TB, fixture fixture, network, address string) observed {
	t.Helper()
	select {
	case event := <-fixture.seen:
		if event.network != network || event.address != address {
			t.Fatalf("destination changed: %+v", event)
		}
		return event
	case <-time.After(3 * time.Second):
		t.Fatal("no local Shadowsocks traffic")
		return observed{}
	}
}

func TestTCPPrefixAndOpaqueDestination(t *testing.T) {
	for _, method := range []string{"aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305"} {
		t.Run(method, func(t *testing.T) {
			fixture := mockShadowsocks(t, method)
			s := startFixtureAdapter(t, fixture.config)
			conn, _ := socksClient(t, s.Port(), fixture.config, 1, "no-local-dns.invalid:443")
			payload := []byte("synthetic TCP echo")
			conn.Write(payload)
			got := make([]byte, len(payload))
			if _, err := io.ReadFull(conn, got); err != nil || !bytes.Equal(payload, got) {
				t.Fatalf("TCP echo: %q %v", got, err)
			}
			event := expectObserved(t, fixture, "tcp", "no-local-dns.invalid:443")
			prefix, _ := base64.StdEncoding.DecodeString(fixture.config.PrefixBase64)
			if !bytes.HasPrefix(event.salt, prefix) {
				t.Fatal("TCP salt prefix missing")
			}
		})
	}
}

func TestAuthenticationRejectsAnonymousAndWrongPassword(t *testing.T) {
	fixture := mockShadowsocks(t, "chacha20-ietf-poly1305")
	s := startFixtureAdapter(t, fixture.config)
	for _, wrong := range []bool{false, true} {
		conn, err := net.Dial("tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(s.Port())))
		if err != nil {
			t.Fatal(err)
		}
		conn.SetDeadline(time.Now().Add(time.Second))
		var result [2]byte
		if !wrong {
			conn.Write([]byte{5, 1, 0})
			io.ReadFull(conn, result[:])
			if result != [2]byte{5, 255} {
				t.Fatal("anonymous access accepted")
			}
		} else {
			conn.Write([]byte{5, 1, 2})
			io.ReadFull(conn, result[:])
			conn.Write([]byte{1, 1, 'x', 1, 'x'})
			io.ReadFull(conn, result[:])
			if result != [2]byte{1, 1} {
				t.Fatal("wrong credentials accepted")
			}
		}
		conn.Close()
	}
	select {
	case <-fixture.seen:
		t.Fatal("unauthenticated outbound traffic")
	case <-time.After(50 * time.Millisecond):
	}
}

func TestUDPAuthenticationOwnerAndLifecycle(t *testing.T) {
	fixture := mockShadowsocks(t, "chacha20-ietf-poly1305")
	s := startFixtureAdapter(t, fixture.config)
	control, bound := socksClient(t, s.Port(), fixture.config, 3, "remote.invalid:53")
	address, _ := encodeAddress("dns.invalid:53")
	payload := []byte("synthetic UDP echo")
	packet := append(append([]byte{0, 0, 0}, address...), payload...)
	// A separate, unauthenticated process must not claim the first packet.
	child := exec.Command(os.Args[0], "-test.run=^TestForeignUDPSender$")
	child.Env = append(os.Environ(), "FLOWVPN_TEST_UDP="+bound, "FLOWVPN_TEST_PACKET="+base64.StdEncoding.EncodeToString(packet))
	if output, err := child.CombinedOutput(); err != nil {
		t.Fatalf("foreign sender: %s %v", output, err)
	}
	select {
	case <-fixture.seen:
		t.Fatal("foreign process used authenticated relay")
	default:
	}
	remote, err := net.ResolveUDPAddr("udp4", bound)
	if err != nil {
		t.Fatal(err)
	}
	client, err := net.DialUDP("udp4", nil, remote)
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	client.SetDeadline(time.Now().Add(3 * time.Second))
	client.Write(packet)
	buffer := make([]byte, 1024)
	n, err := client.Read(buffer)
	if err != nil || !bytes.Equal(packet, buffer[:n]) {
		t.Fatalf("UDP echo: %v", err)
	}
	event := expectObserved(t, fixture, "udp", "dns.invalid:53")
	prefix, _ := base64.StdEncoding.DecodeString(fixture.config.PrefixBase64)
	if bytes.HasPrefix(event.salt, prefix) {
		t.Fatal("legacy TCP prefix incorrectly applied to UDP")
	}
	// Once bound, another local endpoint in even this process is not accepted.
	other, _ := net.DialUDP("udp4", nil, remote)
	defer other.Close()
	other.SetDeadline(time.Now().Add(150 * time.Millisecond))
	other.Write(packet)
	if _, err := other.Read(buffer); err == nil {
		t.Fatal("association changed endpoint")
	}
	control.Close()
	time.Sleep(30 * time.Millisecond)
	client.SetDeadline(time.Now().Add(150 * time.Millisecond))
	client.Write(packet)
	if _, err := client.Read(buffer); err == nil {
		t.Fatal("UDP association survived control close")
	}
}

func TestForeignUDPSender(t *testing.T) {
	bound := os.Getenv("FLOWVPN_TEST_UDP")
	if bound == "" {
		t.Skip("subprocess fixture")
	}
	packet, err := base64.StdEncoding.DecodeString(os.Getenv("FLOWVPN_TEST_PACKET"))
	if err != nil {
		t.Fatal(err)
	}
	address, err := net.ResolveUDPAddr("udp4", bound)
	if err != nil {
		t.Fatal(err)
	}
	conn, err := net.DialUDP("udp4", nil, address)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(200 * time.Millisecond))
	conn.Write(packet)
	var buffer [1024]byte
	if _, err := conn.Read(buffer[:]); err == nil {
		t.Fatal("foreign process received a reply")
	}
}

func TestStdinEOFCancelsSession(t *testing.T) {
	fixture := mockShadowsocks(t, "chacha20-ietf-poly1305")
	input, owner := io.Pipe()
	output, writer := io.Pipe()
	defer output.Close()
	defer writer.Close()
	done := make(chan error, 1)
	go func() { done <- run(context.Background(), input, writer) }()
	data, _ := json.Marshal(fixture.config)
	owner.Write(append(data, '\n'))
	line, err := bufio.NewReader(output).ReadBytes('\n')
	if err != nil {
		t.Fatal(err)
	}
	var ready struct {
		ProtocolVersion int `json:"protocolVersion"`
		Port            int `json:"port"`
	}
	if json.Unmarshal(line, &ready) != nil || ready.ProtocolVersion != 1 || ready.Port == 0 {
		t.Fatal("invalid readiness")
	}
	conn, _ := socksClient(t, ready.Port, fixture.config, 1, "example.invalid:443")
	owner.Close()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("EOF did not stop adapter")
	}
	conn.SetReadDeadline(time.Now().Add(time.Second))
	var b [1]byte
	if _, err := conn.Read(b[:]); err == nil {
		t.Fatal("stream survived shutdown")
	}
}
