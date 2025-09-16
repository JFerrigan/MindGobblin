// Authorization token that must have been created previously. See : https://developer.spotify.com/documentation/web-api/concepts/authorization
const token = 'BQD2zQ1JOnlrFDEMKLehcxW6L7dpkfII5m8qVq5lt0y3BD6TUVbIf33k6HSOEK4H9g0pCaFAZAt8qS6OwCDsm2HcIF-y8uxgIvZrUO-h9LA8x0dXjSyZsyhHocwFp89ycB2oDRaT_022Lq5B8bp20YdaAt-htsNT1y25HCxI40xCClTc8RVy0XCEKI1Ij_DHRoRzSA2BknEj1tZt2OWAoYHRdbs5NxNVhcpd_x6vTFj8SnXr6aPxNUJ2im9imtv79VYkSv0hSRGK5yzCksoTbEAfm9lCLLFeqgLJjKHBzgGaTQI';
async function fetchWebApi(endpoint, method, body) {
  const res = await fetch(`https://api.spotify.com/${endpoint}`, {
    headers: {
      Authorization: `Bearer ${token}`,
    },
    method,
    body:JSON.stringify(body)
  });
  return await res.json();
}

async function getTopTracks(){
  // Endpoint reference : https://developer.spotify.com/documentation/web-api/reference/get-users-top-artists-and-tracks
  return (await fetchWebApi(
    'v1/me/top/tracks?time_range=long_term&limit=5', 'GET'
  )).items;
}

const topTracks = await getTopTracks();
console.log(
  topTracks?.map(
    ({name, artists}) =>
      `${name} by ${artists.map(artist => artist.name).join(', ')}`
  )
);


///
///
///

// Authorization token that must have been created previously. See : https://developer.spotify.com/documentation/web-api/concepts/authorization
const token = 'BQD2zQ1JOnlrFDEMKLehcxW6L7dpkfII5m8qVq5lt0y3BD6TUVbIf33k6HSOEK4H9g0pCaFAZAt8qS6OwCDsm2HcIF-y8uxgIvZrUO-h9LA8x0dXjSyZsyhHocwFp89ycB2oDRaT_022Lq5B8bp20YdaAt-htsNT1y25HCxI40xCClTc8RVy0XCEKI1Ij_DHRoRzSA2BknEj1tZt2OWAoYHRdbs5NxNVhcpd_x6vTFj8SnXr6aPxNUJ2im9imtv79VYkSv0hSRGK5yzCksoTbEAfm9lCLLFeqgLJjKHBzgGaTQI';
async function fetchWebApi(endpoint, method, body) {
  const res = await fetch(`https://api.spotify.com/${endpoint}`, {
    headers: {
      Authorization: `Bearer ${token}`,
    },
    method,
    body:JSON.stringify(body)
  });
  return await res.json();
}

const tracksUri = [
  'spotify:track:2FK6SnUKJY8m8kVBmdQbSM','spotify:track:1Pt7RPrjEQfzpPA9PS5aZj','spotify:track:1WDCLJlhvMB72DN7V8p7Fz','spotify:track:35e4fhZkQuQo77kOro2QYF','spotify:track:3Jc5Wiu0MK1IkA7AJK3BOS'
];

async function createPlaylist(tracksUri){
  const { id: user_id } = await fetchWebApi('v1/me', 'GET')

  const playlist = await fetchWebApi(
    `v1/users/${user_id}/playlists`, 'POST', {
      "name": "My top tracks playlist",
      "description": "Playlist created by the tutorial on developer.spotify.com",
      "public": false
  })

  await fetchWebApi(
    `v1/playlists/${playlist.id}/tracks?uris=${tracksUri.join(',')}`,
    'POST'
  );

  return playlist;
}

const createdPlaylist = await createPlaylist(tracksUri);
console.log(createdPlaylist.name, createdPlaylist.id);

///
///
///

const playlistId = '1bZyuFnTTjhONclR0DXfql';

<iframe
  title="Spotify Embed: Recommendation Playlist "
  src={`https://open.spotify.com/embed/playlist/1bZyuFnTTjhONclR0DXfql?utm_source=generator&theme=0`}
  width="100%"
  height="100%"
  style={{ minHeight: '360px' }}
  frameBorder="0"
  allow="autoplay; clipboard-write; encrypted-media; fullscreen; picture-in-picture"
  loading="lazy"
/>