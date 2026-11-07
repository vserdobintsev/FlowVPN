// SPDX-License-Identifier: GPL-3.0-only
package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"net/netip"
	"strconv"
	"sync"
	"time"

	"golang.getoutline.org/sdk/transport"
)

const (
	maxSessions       = 256
	handshakeTimeout  = 10 * time.Second
	idleTimeout       = 2 * time.Minute
	streamIdleTimeout = 10 * time.Minute
	maxDatagram       = 16*1024 - 512 // Room for the SDK's salt, tag and SOCKS address.
)

var errSocks = errors.New("invalid SOCKS request")

type server struct {
	listener               *net.TCPListener
	out                    outbound
	userHash, passwordHash [32]byte
	ctx                    context.Context
	cancel                 context.CancelFunc
	mu                     sync.Mutex
	clients                map[*net.TCPConn]struct{}
	closed                 bool
	workers                sync.WaitGroup
	done                   chan struct{}
	slots                  chan struct{}
}

func newServer(username, password string, out outbound) (*server, error) {
	listener, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithCancel(context.Background())
	return &server{listener: listener, out: out, userHash: sha256.Sum256([]byte(username)), passwordHash: sha256.Sum256([]byte(password)), ctx: ctx, cancel: cancel, clients: make(map[*net.TCPConn]struct{}), done: make(chan struct{}), slots: make(chan struct{}, maxSessions)}, nil
}

func (s *server) Port() int             { return s.listener.Addr().(*net.TCPAddr).Port }
func (s *server) Done() <-chan struct{} { return s.done }

func (s *server) Serve() error {
	defer func() { s.Close(); s.workers.Wait(); close(s.done) }()
	for {
		client, err := s.listener.AcceptTCP()
		if err != nil {
			if s.ctx.Err() != nil {
				return nil
			}
			return err
		}
		if !client.RemoteAddr().(*net.TCPAddr).IP.Equal(net.IPv4(127, 0, 0, 1)) {
			client.Close()
			continue
		}
		select {
		case s.slots <- struct{}{}:
		default:
			client.Close()
			continue
		}
		s.mu.Lock()
		if s.closed {
			s.mu.Unlock()
			client.Close()
			<-s.slots
			return nil
		}
		s.clients[client] = struct{}{}
		s.workers.Add(1)
		s.mu.Unlock()
		go func() {
			defer s.workers.Done()
			defer func() { client.Close(); s.mu.Lock(); delete(s.clients, client); s.mu.Unlock(); <-s.slots }()
			s.handle(client)
		}()
	}
}

func (s *server) Close() {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return
	}
	s.closed = true
	s.cancel()
	s.listener.Close()
	for client := range s.clients {
		client.Close()
	}
}

func (s *server) handle(client *net.TCPConn) {
	client.SetDeadline(time.Now().Add(handshakeTimeout))
	if !s.authenticate(client) {
		return
	}
	var header [3]byte
	if _, err := io.ReadFull(client, header[:]); err != nil || header[0] != 5 || header[2] != 0 {
		return
	}
	address, _, err := readAddress(client)
	if err != nil {
		reply(client, 8, 0)
		return
	}
	client.SetDeadline(time.Time{})
	switch header[1] {
	case 1:
		_, port, err := net.SplitHostPort(address)
		if err != nil || port == "0" {
			reply(client, 8, 0)
			return
		}
		upstream, err := s.out.dial(s.ctx, address)
		if err != nil {
			reply(client, 5, 0)
			return
		}
		defer upstream.Close()
		stop := context.AfterFunc(s.ctx, func() { upstream.Close() })
		defer stop()
		if reply(client, 0, 0) != nil {
			return
		}
		relayStream(client, upstream)
	case 3:
		// Clients (including sing-box) may put a destination or 0.0.0.0:0 in
		// UDP ASSOCIATE, not their local UDP endpoint. Determine ownership from
		// Windows instead; never trust/resolve this request address.
		owner, err := tcpClientOwner(client)
		if err != nil {
			reply(client, 2, 0)
			return
		}
		s.serveUDP(client, owner)
	default:
		reply(client, 7, 0)
	}
}

func (s *server) authenticate(client net.Conn) bool {
	var header [2]byte
	if _, err := io.ReadFull(client, header[:]); err != nil || header[0] != 5 || header[1] == 0 {
		return false
	}
	methods := make([]byte, int(header[1]))
	if _, err := io.ReadFull(client, methods); err != nil {
		return false
	}
	if !bytes.Contains(methods, []byte{2}) {
		client.Write([]byte{5, 255})
		return false
	}
	if _, err := client.Write([]byte{5, 2}); err != nil {
		return false
	}
	if _, err := io.ReadFull(client, header[:]); err != nil || header[0] != 1 || header[1] == 0 || header[1] > 128 {
		return false
	}
	username := make([]byte, int(header[1]))
	defer clear(username)
	if _, err := io.ReadFull(client, username); err != nil {
		return false
	}
	var length [1]byte
	if _, err := io.ReadFull(client, length[:]); err != nil || length[0] == 0 || length[0] > 128 {
		return false
	}
	password := make([]byte, int(length[0]))
	defer clear(password)
	if _, err := io.ReadFull(client, password); err != nil {
		return false
	}
	userHash := sha256.Sum256(username)
	passwordHash := sha256.Sum256(password)
	valid := subtle.ConstantTimeCompare(userHash[:], s.userHash[:]) & subtle.ConstantTimeCompare(passwordHash[:], s.passwordHash[:])
	status := byte(1)
	if valid == 1 {
		status = 0
	}
	_, err := client.Write([]byte{1, status})
	return valid == 1 && err == nil
}

func reply(writer io.Writer, code byte, port int) error {
	_, err := writer.Write([]byte{5, code, 0, 1, 127, 0, 0, 1, byte(port >> 8), byte(port)})
	return err
}

func readAddress(reader io.Reader) (string, []byte, error) {
	var first [1]byte
	if _, err := io.ReadFull(reader, first[:]); err != nil {
		return "", nil, err
	}
	raw := []byte{first[0]}
	var host string
	switch first[0] {
	case 1, 4:
		size := 4
		if first[0] == 4 {
			size = 16
		}
		body := make([]byte, size)
		if _, err := io.ReadFull(reader, body); err != nil {
			return "", nil, err
		}
		address, ok := netip.AddrFromSlice(body)
		if !ok {
			return "", nil, errSocks
		}
		host = address.String()
		raw = append(raw, body...)
	case 3:
		if _, err := io.ReadFull(reader, first[:]); err != nil || first[0] == 0 {
			return "", nil, errSocks
		}
		body := make([]byte, int(first[0]))
		if _, err := io.ReadFull(reader, body); err != nil {
			return "", nil, err
		}
		for _, b := range body {
			if b < 33 || b > 126 || bytes.ContainsRune([]byte("[]:/\\@?#"), rune(b)) {
				return "", nil, errSocks
			}
		}
		host = string(body)
		raw = append(raw, first[0])
		raw = append(raw, body...)
	default:
		return "", nil, errSocks
	}
	var port [2]byte
	if _, err := io.ReadFull(reader, port[:]); err != nil {
		return "", nil, err
	}
	raw = append(raw, port[:]...)
	return net.JoinHostPort(host, strconv.Itoa(int(binary.BigEndian.Uint16(port[:])))), raw, nil
}

func relayStream(client *net.TCPConn, upstream transport.StreamConn) {
	relayStreamWithTimeouts(client, upstream, streamIdleTimeout, dialTimeout)
}

// Serialize deadline updates with the half-close transition. An atomic timestamp
// alone would still let an in-flight SetReadDeadline overwrite the shorter limit.
type relayDeadlines struct {
	mu     sync.Mutex
	cutoff time.Time
}

func (d *relayDeadlines) read(connection net.Conn, idle time.Duration) {
	d.mu.Lock()
	defer d.mu.Unlock()
	deadline := time.Now().Add(idle)
	if !d.cutoff.IsZero() && d.cutoff.Before(deadline) {
		deadline = d.cutoff
	}
	connection.SetReadDeadline(deadline)
}

func (d *relayDeadlines) write(connection net.Conn) {
	d.mu.Lock()
	defer d.mu.Unlock()
	deadline := time.Now().Add(dialTimeout)
	if !d.cutoff.IsZero() && d.cutoff.Before(deadline) {
		deadline = d.cutoff
	}
	connection.SetWriteDeadline(deadline)
}

func (d *relayDeadlines) halfClose(client, upstream net.Conn, timeout time.Duration) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if !d.cutoff.IsZero() {
		return
	}
	d.cutoff = time.Now().Add(timeout)
	client.SetDeadline(d.cutoff)
	upstream.SetDeadline(d.cutoff)
}

func relayStreamWithTimeouts(client *net.TCPConn, upstream transport.StreamConn, idle, halfClose time.Duration) {
	results := make(chan error, 2)
	var deadlines relayDeadlines
	copyOne := func(dst net.Conn, src net.Conn, closeWrite func() error) {
		buffer := make([]byte, 32*1024)
		for {
			deadlines.read(src, idle)
			n, err := src.Read(buffer)
			if n > 0 {
				deadlines.write(dst)
				if written, writeErr := dst.Write(buffer[:n]); writeErr != nil {
					results <- writeErr
					return
				} else if written != n {
					results <- io.ErrShortWrite
					return
				}
			}
			if err != nil {
				if errors.Is(err, io.EOF) {
					// Start the shared budget at EOF, before an SDK CloseWrite can flush pending bytes.
					deadlines.halfClose(client, upstream, halfClose)
					deadlines.write(dst)
					closeWrite()
					results <- nil
				} else {
					results <- err
				}
				return
			}
		}
	}
	go copyOne(upstream, client, upstream.CloseWrite)
	go copyOne(client, upstream, client.CloseWrite)
	if err := <-results; err != nil {
		client.Close()
		upstream.Close()
	} else {
		// Permit a final response after half-close without keeping a dead peer
		// alive for the full idle timeout.
		deadlines.halfClose(client, upstream, halfClose)
	}
	<-results
}

func (s *server) serveUDP(control *net.TCPConn, owner uint32) {
	local, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		reply(control, 1, 0)
		return
	}
	defer local.Close()
	ctx, cancel := context.WithTimeout(s.ctx, dialTimeout)
	upstream, err := s.out.packet.ListenPacket(ctx)
	cancel()
	if err != nil {
		reply(control, 1, 0)
		return
	}
	defer upstream.Close()
	stop := context.AfterFunc(s.ctx, func() { local.Close(); upstream.Close(); control.Close() })
	defer stop()
	if reply(control, 0, local.LocalAddr().(*net.UDPAddr).Port) != nil {
		return
	}
	var endpointMu sync.Mutex
	var endpoint netip.AddrPort
	var workers sync.WaitGroup
	finish := func() { local.Close(); upstream.Close(); control.Close() }
	workers.Add(2)
	go func() {
		defer workers.Done()
		defer finish()
		buffer := make([]byte, maxDatagram+263)
		for {
			local.SetReadDeadline(time.Now().Add(idleTimeout))
			n, from, err := local.ReadFromUDPAddrPort(buffer)
			if err != nil {
				return
			}
			if from.Addr() != netip.MustParseAddr("127.0.0.1") || n < 7 || buffer[0] != 0 || buffer[1] != 0 || buffer[2] != 0 {
				continue
			}
			reader := bytes.NewReader(buffer[3:n])
			address, _, err := readAddress(reader)
			if err != nil || reader.Len() > maxDatagram {
				continue
			}
			_, port, _ := net.SplitHostPort(address)
			if port == "0" {
				continue
			}
			endpointMu.Lock()
			bound := endpoint
			endpointMu.Unlock()
			if bound.IsValid() && from != bound {
				continue
			}
			// Verify every datagram, not just the first: a different process must
			// not inherit relay access by rebinding a released client UDP port.
			if !udpOwnedBy(from, owner) {
				continue
			}
			if !bound.IsValid() {
				endpointMu.Lock()
				endpoint = from
				endpointMu.Unlock()
			}
			destination, err := transport.MakeNetAddr("udp", address)
			if err != nil {
				continue
			}
			upstream.SetWriteDeadline(time.Now().Add(dialTimeout))
			if _, err = upstream.WriteTo(buffer[n-reader.Len():n], destination); err != nil {
				return
			}
		}
	}()
	go func() {
		defer workers.Done()
		defer finish()
		buffer := make([]byte, maxDatagram)
		packetBuffer := make([]byte, 3+259+maxDatagram)
		for {
			upstream.SetReadDeadline(time.Now().Add(idleTimeout))
			n, from, err := upstream.ReadFrom(buffer)
			if err != nil {
				return
			}
			endpointMu.Lock()
			destination := endpoint
			endpointMu.Unlock()
			if !destination.IsValid() || !udpOwnedBy(destination, owner) {
				continue
			}
			header, err := encodeAddress(from.String())
			if err != nil {
				continue
			}
			packet := packetBuffer[:3+len(header)+n]
			copy(packet[3:], header)
			copy(packet[3+len(header):], buffer[:n])
			local.SetWriteDeadline(time.Now().Add(dialTimeout))
			if _, err = local.WriteToUDPAddrPort(packet, destination); err != nil {
				return
			}
		}
	}()
	var extra [1]byte
	control.Read(extra[:]) // EOF or unexpected control data ends the association.
	finish()
	workers.Wait()
}

func encodeAddress(address string) ([]byte, error) {
	host, portText, err := net.SplitHostPort(address)
	if err != nil {
		return nil, errSocks
	}
	port, err := strconv.Atoi(portText)
	if err != nil || port < 1 || port > 65535 {
		return nil, errSocks
	}
	var result []byte
	if ip, err := netip.ParseAddr(host); err == nil {
		if ip.Is4() {
			result = append([]byte{1}, ip.AsSlice()...)
		} else {
			result = append([]byte{4}, ip.AsSlice()...)
		}
	} else {
		if len(host) == 0 || len(host) > 255 {
			return nil, errSocks
		}
		result = append([]byte{3, byte(len(host))}, host...)
	}
	return append(result, byte(port>>8), byte(port)), nil
}
