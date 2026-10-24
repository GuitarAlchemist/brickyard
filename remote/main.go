// delta-remote shows Delta's window to one device at a time, over WebRTC.
//
//	delta-remote [-ip <address>] [-port 8810] [-udp 8811] [-cert <file> -key <file> -name <host>] [-forget-devices]
//	             [-guest-host <name> [-guest-port 8812] [-local-port 8813] [-turn <file>] [-mic <device>]]
//
// Each start prints a link, https://<host>:<port>/#code=<code>, that pairs one device within 100 s. Then a new code
// replaces it, as soon as a device uses it too: Delta's window shows the current one as a QR, and link.txt holds it. A
// paired device needs no code afterwards. -forget-devices unpairs them all.
//
// Without -cert, the certificate is the gateway's own: the browser warns about it once, and a device on the same
// network could stand in for the gateway while you accept that warning. Give a certificate the device trusts, such as
// the one `tailscale cert` makes for this machine's tailnet name, with -cert, -key and -name, to rule that out.
// It listens on one address only, and WebRTC uses that address and one UDP port.
//
// With -guest-host, a paired device can also invite guests from outside the network to watch, and only watch: a
// Cloudflare tunnel brings https://<guest-host> to the guests' entry on 127.0.0.1:<guest-port>, which serves nothing
// else (guests.go), and their video goes through Cloudflare's TURN relay, with the key held in -turn. Delta's window
// invites too, through its own entry on 127.0.0.1:<local-port>, which only the brain's secret opens. With -mic,
// guests also hear that microphone and Delta's voice, while any of them watches; they cannot speak.
package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"flag"
	"fmt"
	"math/big"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/webrtc/v4"
)

type options struct {
	ip, certFile, keyFile, name string
	port, udpPort               int
	title, exe, ffmpeg          string
	brain, brainSecret          string
	forget                      bool
	guestHost, turnFile, mic    string
	guestPort, localPort        int
}

func main() {
	var o options
	flag.StringVar(&o.ip, "ip", "", "the address to serve on (default: the LAN address of the default route)")
	flag.IntVar(&o.port, "port", 8810, "the HTTPS port")
	flag.IntVar(&o.udpPort, "udp", 8811, "the one UDP port WebRTC uses")
	flag.StringVar(&o.certFile, "cert", "", "a certificate the devices trust (PEM); default: the gateway's own")
	flag.StringVar(&o.keyFile, "key", "", "with -cert, its key (PEM)")
	flag.StringVar(&o.name, "name", "", "with -cert, the host name it is for (default: the address)")
	flag.StringVar(&o.title, "title", "Brickyard", "the title of the window shown")
	flag.StringVar(&o.exe, "exe", "scene.exe", "the executable that owns that window")
	flag.StringVar(&o.ffmpeg, "ffmpeg", "ffmpeg", "ffmpeg 8 or later, with gfxcapture and libx264")
	flag.StringVar(&o.brain, "brain", "http://127.0.0.1:8807", "Delta's brain, for voice and the QR")
	flag.StringVar(&o.brainSecret, "brain-secret", filepath.Join(os.Getenv("LOCALAPPDATA"), "delta", "remote.secret"),
		"the secret the brain writes at each start")
	flag.BoolVar(&o.forget, "forget-devices", false, "unpair every device first")
	flag.StringVar(&o.guestHost, "guest-host", "", "the host name a tunnel brings to the guests' entry; default: no guests")
	flag.IntVar(&o.guestPort, "guest-port", 8812, "the guests' entry, on 127.0.0.1 only, for the tunnel")
	flag.IntVar(&o.localPort, "local-port", 8813, "Delta's window's entry for invitations, on 127.0.0.1 only")
	flag.StringVar(&o.mic, "mic", "", `the microphone guests hear, by its DirectShow name ("ffmpeg -list_devices true -f dshow -i dummy"); default: none`)
	flag.StringVar(&o.turnFile, "turn", filepath.Join(os.Getenv("LOCALAPPDATA"), "delta-remote", "turn.json"),
		`Cloudflare's TURN key for the guests' video, as {"id": "…", "token": "…"}`)
	flag.Parse()

	if err := run(o); err != nil {
		fmt.Fprintln(os.Stderr, "delta-remote:", err)
		os.Exit(1)
	}
}

func run(o options) error {
	ip, err := lanIP(o.ip)
	if err != nil {
		return err
	}
	home := filepath.Join(os.Getenv("LOCALAPPDATA"), "delta-remote")
	if err := os.MkdirAll(home, 0o700); err != nil {
		return err
	}
	if o.forget {
		if err := forgetDevices(home); err != nil {
			return err
		}
	}
	name := ip.String()
	var cert tls.Certificate
	if o.certFile != "" {
		if cert, err = tls.LoadX509KeyPair(o.certFile, o.keyFile); err != nil {
			return err
		}
		if o.name != "" {
			name = o.name
		}
	} else if cert, err = ownCertificate(home, ip); err != nil {
		return err
	}

	udp, err := net.ListenUDP("udp4", &net.UDPAddr{IP: ip, Port: o.udpPort})
	if err != nil {
		return fmt.Errorf("UDP %s:%d: %w", ip, o.udpPort, err)
	}
	se := webrtc.SettingEngine{}
	se.SetICEUDPMux(webrtc.NewICEUDPMux(nil, udp))
	se.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
	se.SetIPFilter(func(a net.IP) bool { return a.Equal(ip) })
	se.SetIncludeLoopbackCandidate(ip.IsLoopback()) // -ip 127.0.0.1: a browser on this machine, for a check
	s, err := newServerWith(home, se)
	if err != nil {
		return err
	}
	s.host = net.JoinHostPort(name, strconv.Itoa(o.port))
	s.brain = newBrain(o.brain, o.brainSecret)
	c, err := newCapture(o.ffmpeg, o.title, o.exe, s.video)
	if err != nil {
		return err
	}
	s.onPeer = c.run

	if o.guestHost != "" {
		// Guests are not on this network: their WebRTC goes through the relay, from this address and any UDP port.
		gse := webrtc.SettingEngine{}
		gse.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
		gse.SetIPFilter(func(a net.IP) bool { return a.Equal(ip) })
		gse.SetICEMulticastDNSMode(ice.MulticastDNSModeDisabled)
		s.guestAPI = webrtc.NewAPI(webrtc.WithSettingEngine(gse))
		s.guestHost = o.guestHost
		s.ice = cloudflareRelay(o.turnFile)
		s.watching = make(chan int, 1)
		if o.mic != "" {
			voice, err := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{
				MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2, SDPFmtpLine: "minptime=10;useinbandfec=1",
			}, "voice", "delta")
			if err != nil {
				return err
			}
			mic, err := newMicCapture(o.ffmpeg, o.mic, voice)
			if err != nil {
				return err
			}
			s.voice, s.onGuests = voice, mic.run
		}
		go s.announce()
		guests := &http.Server{
			Addr:              net.JoinHostPort("127.0.0.1", strconv.Itoa(o.guestPort)),
			Handler:           s.guestHandler(),
			ReadHeaderTimeout: 5 * time.Second,
		}
		go func() { logf("the guests' entry: %v", guests.ListenAndServe()) }()
		s.localHost = net.JoinHostPort("127.0.0.1", strconv.Itoa(o.localPort))
		local := &http.Server{Addr: s.localHost, Handler: s.localHandler(), ReadHeaderTimeout: 5 * time.Second}
		go func() { logf("the window's entry: %v", local.ListenAndServe()) }()
		fmt.Printf("Guests invited from a paired device watch at https://%s, through a tunnel to %s\n", o.guestHost, guests.Addr)
	}

	s.linkFile = filepath.Join(home, "link.txt")
	link := fmt.Sprintf("https://%s/#code=%s", s.host, s.issueCode())
	fmt.Printf("To pair a device, scan the QR in Delta's window, or open within %d s: %s\n",
		int(renewEvery/time.Second), link)
	fmt.Println("A new link replaces it then, in", s.linkFile)

	srv := &http.Server{
		Addr:              net.JoinHostPort(ip.String(), strconv.Itoa(o.port)),
		Handler:           s.handler(),
		TLSConfig:         &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS12},
		ReadHeaderTimeout: 5 * time.Second,
	}
	return srv.ListenAndServeTLS("", "")
}

// lanIP is the given address, or the source address of the default route: a UDP "connection" sends nothing, but
// picks the interface a packet to the internet would leave by, which skips WSL's and Hyper-V's virtual adapters.
func lanIP(given string) (net.IP, error) {
	if given != "" {
		ip := net.ParseIP(given).To4()
		if ip == nil {
			return nil, fmt.Errorf("-ip %q is not an IPv4 address", given)
		}
		return ip, nil
	}
	c, err := net.Dial("udp4", "192.0.2.1:9")
	if err != nil {
		return nil, fmt.Errorf("no default route; give -ip: %w", err)
	}
	defer c.Close()
	ip := c.LocalAddr().(*net.UDPAddr).IP.To4()
	if ip == nil || !ip.IsPrivate() {
		return nil, fmt.Errorf("the default route's address %v is not a private LAN address; give -ip", ip)
	}
	return ip, nil
}

// ownCertificate is the gateway's own certificate for ip, made once and remade when the address changes. Safari
// takes it only as ECDSA P-256 with the address as a SAN, for server auth, and for at most 825 days.
func ownCertificate(home string, ip net.IP) (tls.Certificate, error) {
	certPath, keyPath := filepath.Join(home, "cert.pem"), filepath.Join(home, "key.pem")
	if c, err := tls.LoadX509KeyPair(certPath, keyPath); err == nil {
		if leaf, err := x509.ParseCertificate(c.Certificate[0]); err == nil &&
			time.Now().Before(leaf.NotAfter) && len(leaf.IPAddresses) == 1 && leaf.IPAddresses[0].Equal(ip) {
			return c, nil
		}
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}
	serial, _ := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 62))
	tmpl := &x509.Certificate{
		SerialNumber: serial,
		Subject:      pkix.Name{CommonName: "delta-remote " + ip.String()},
		IPAddresses:  []net.IP{ip},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().AddDate(1, 0, 0),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &key.PublicKey, key)
	if err != nil {
		return tls.Certificate{}, err
	}
	keyDer, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		return tls.Certificate{}, err
	}
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDer})
	if err := os.WriteFile(certPath, certPEM, 0o600); err != nil {
		return tls.Certificate{}, err
	}
	if err := os.WriteFile(keyPath, keyPEM, 0o600); err != nil {
		return tls.Certificate{}, err
	}
	return tls.X509KeyPair(certPEM, keyPEM)
}
