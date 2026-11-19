package main

import (
	"net"
	"slices"
	"strconv"
	"strings"
	"time"

	"golang.org/x/net/dns/dnsmessage"
)

// A Chromecast says where it is over multicast DNS, as _googlecast._tcp.local. The gateway asks from a port of its
// own, not 5353, so that each Chromecast answers it directly (RFC 6762's legacy unicast), and needs no port that
// Windows or a browser may already hold.

const googlecast = "_googlecast._tcp.local."

// chromecast is one found on the network: the name its owner gave it, and where its Cast protocol listens.
type chromecast struct {
	Name string `json:"name"`
	addr string
}

var mdnsGroup = &net.UDPAddr{IP: net.IPv4(224, 0, 0, 251), Port: 5353}

// findChromecasts asks the network from ip and gathers the answers for wait.
func findChromecasts(ip net.IP, wait time.Duration) ([]chromecast, error) {
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: ip})
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	query, err := googlecastQuery()
	if err != nil {
		return nil, err
	}
	if _, err := conn.WriteToUDP(query, mdnsGroup); err != nil {
		return nil, err
	}
	conn.SetReadDeadline(time.Now().Add(wait))
	var answers [][]byte
	buf := make([]byte, 9000)
	for {
		n, _, err := conn.ReadFromUDP(buf)
		if err != nil {
			break // the deadline
		}
		answers = append(answers, slices.Clone(buf[:n]))
	}
	return chromecastsIn(answers), nil
}

func googlecastQuery() ([]byte, error) {
	b := dnsmessage.NewBuilder(nil, dnsmessage.Header{ID: 0x6465})
	b.StartQuestions()
	b.Question(dnsmessage.Question{
		Name: dnsmessage.MustNewName(googlecast), Type: dnsmessage.TypePTR, Class: dnsmessage.ClassINET,
	})
	return b.Finish()
}

// chromecastsIn reads the Chromecasts out of mDNS answers: each instance that a PTR names, with its SRV's port, its
// target's address and its TXT's friendly name (fn=), sorted by name. One without an address is left out, and so is an
// answer that does not parse.
func chromecastsIn(answers [][]byte) []chromecast {
	instances := map[string]bool{}
	type service struct {
		target string
		port   uint16
	}
	services := map[string]service{}
	names := map[string]string{}
	hosts := map[string]net.IP{}
	for _, raw := range answers {
		var m dnsmessage.Message
		if m.Unpack(raw) != nil {
			continue
		}
		for _, r := range slices.Concat(m.Answers, m.Authorities, m.Additionals) {
			name := strings.ToLower(r.Header.Name.String())
			switch b := r.Body.(type) {
			case *dnsmessage.PTRResource:
				if name == googlecast {
					instances[strings.ToLower(b.PTR.String())] = true
				}
			case *dnsmessage.SRVResource:
				services[name] = service{strings.ToLower(b.Target.String()), b.Port}
			case *dnsmessage.TXTResource:
				for _, t := range b.TXT {
					if v, ok := strings.CutPrefix(t, "fn="); ok {
						names[name] = v
					}
				}
			case *dnsmessage.AResource:
				hosts[name] = net.IP(slices.Clone(b.A[:]))
			}
		}
	}
	var found []chromecast
	for instance := range instances {
		s, ok := services[instance]
		ip := hosts[s.target]
		if !ok || ip == nil {
			continue
		}
		name := names[instance]
		if name == "" {
			name = strings.TrimSuffix(instance, "."+googlecast)
		}
		found = append(found, chromecast{Name: name, addr: net.JoinHostPort(ip.String(), strconv.Itoa(int(s.port)))})
	}
	slices.SortFunc(found, func(a, b chromecast) int { return strings.Compare(a.Name, b.Name) })
	return found
}
