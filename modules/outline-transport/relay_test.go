// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"io"
	"net"
	"testing"
	"time"
)

func tcpPair(t testing.TB) (*net.TCPConn, *net.TCPConn) {
	t.Helper()
	listener, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	client, err := net.DialTCP("tcp4", nil, listener.Addr().(*net.TCPAddr))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { client.Close() })
	server, err := listener.AcceptTCP()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { server.Close() })
	return client, server
}

func TestHalfCloseDeadlineSurvivesResponseChunks(t *testing.T) {
	client, relayClient := tcpPair(t)
	relayUpstream, upstream := tcpPair(t)
	done := make(chan struct{})
	go func() {
		relayStreamWithTimeouts(relayClient, relayUpstream, 2*time.Second, 200*time.Millisecond)
		close(done)
	}()
	client.SetDeadline(time.Now().Add(time.Second))
	upstream.SetDeadline(time.Now().Add(time.Second))
	if err := client.CloseWrite(); err != nil {
		t.Fatal(err)
	}
	var buffer [1]byte
	if _, err := upstream.Read(buffer[:]); err != io.EOF {
		t.Fatalf("half-close not forwarded: %v", err)
	}
	// Response chunks after EOF must not renew the relay's absolute half-close budget.
	for range 2 {
		time.Sleep(50 * time.Millisecond)
		if _, err := upstream.Write([]byte{42}); err != nil {
			t.Fatal(err)
		}
		if _, err := io.ReadFull(client, buffer[:]); err != nil {
			t.Fatal(err)
		}
	}
	select {
	case <-done:
	case <-time.After(700 * time.Millisecond):
		t.Fatal("response data replaced the absolute half-close deadline with the idle timeout")
	}
}
