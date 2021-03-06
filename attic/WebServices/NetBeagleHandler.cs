//
// NetBeagleHandler.cs
//
// Copyright (C) 2005 Novell, Inc.
//
// Authors:
//	Vijay K. Nanjundaswamy (knvijay@novell.com)
//

//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections;
using System.Threading;

using Beagle;
using Beagle.Util;

namespace Beagle.Daemon
{	

	public class NetBeagleHandler
	{
		string Hostname;
		string Port;
		IQueryable netBeagleQueryable;
		
		BeagleWebService wsp;	
		IQueryResult result;

		static Logger log = Logger.Get ("NetBeagleHandler");
		
		public NetBeagleHandler (string hostname, string port, IQueryable iq)
		{
			this.Hostname = hostname;
			this.Port = port;
			netBeagleQueryable = iq;
			
			log.Info ("Instantiating NetBeagleHandler for " + Hostname + ":" + Port);
			
			wsp = new BeagleWebService (Hostname, Port);
			wsp.Timeout = 40000; //40 sec time limit per request 
		}			
	
		private string[] ICollection2StringList(ICollection il)
		{
			if (il == null)
				return new string[0] ;
			
			string[] sl = new string[il.Count];			
			il.CopyTo(sl, 0);
			
			return sl;
		}
		
		public IAsyncResult DoQuery (Query query,  IQueryResult result,
				     					IQueryableChangeData changeData)
		{
      		ICollection l;
     		
			SearchRequest sreq = new SearchRequest();

      		l = query.Text;
      		if ((l != null) && (l.Count > 0))
				sreq.text = ICollection2StringList(query.Text);
					
      		l = query.MimeTypes;
      		if ((l != null) && (l.Count > 0))					
				sreq.mimeType = ICollection2StringList(query.MimeTypes);

			l = query.Sources;
      		if ((l != null) && (l.Count > 0))						
				sreq.searchSources = ICollection2StringList(query.Sources);
				
			sreq.qdomain = QueryDomain.Global; //Caution: This Enables Cascaded NetBeagle searching !
			//sreq.qdomain = QueryDomain.System;
			
			//Cache the query request, get a unique searchId and include in network searchRequest:
			sreq.searchId = NetworkedBeagle.AddRequest(query);

			
			int hc = NetworkedBeagle.HopCount(query);
			sreq.hopCount = (hc > 0) ? hc:1;

			log.Info("NetBeagleHandler: Starting WebService Query for " + Hostname + ":" + Port);
			
			ReqContext rc = new ReqContext(wsp, result, netBeagleQueryable);
				
			IAsyncResult ar = wsp.BeginBeagleQuery(sreq, DoQueryResponseHandler, rc);

			// Return w/o waiting for Async query to complete.	
			// Return IAsynResult handle, to allow caller to control it, if required. 
					   				
			return ar;
		}

    	public static void DoQueryResponseHandler(IAsyncResult ar) 
    	{   		
    		ReqContext rc = (ReqContext)ar.AsyncState;
    		
			IQueryable 			 iq = rc.GetQueryable;  
    		BeagleWebService 	wsp = rc.GetProxy;  		
    		IQueryResult 	 result = rc.GetResult;
			
			int count = 0;
			//bool hitRejectsLogged = false;
			
    		try
      		{	    		
    			SearchResult resp = wsp.EndBeagleQuery(ar);		

				if ((resp != null) && (resp.numResults > 0))
				{
					if (rc.SearchToken == null)
						rc.SearchToken = resp.searchToken; 
						
					//NetContext nc = new NetContext(wsp, resp.searchToken);						
			   		HitResult[] hres = resp.hitResults;
			   		ArrayList  nwhits = new ArrayList();
		
  					for (int i = 0; i < hres.Length; i++) {
			
						try {	
							HitResult hr = hres[i];
							Hit hit = new NetworkHit();
			
							//[Uri Format] netbeagle://164.99.153.134:8888/searchToken?http:///....	
							if (hr.uri.StartsWith(NetworkedBeagle.BeagleNetPrefix))
								hit.UriAsString = hr.uri;
							else {							
								string[] fragments = hr.uri.Split ('/');
								string hostNamePort = fragments[2];										
								hit.UriAsString = NetworkedBeagle.BeagleNetPrefix + hostNamePort + "/" + resp.searchToken + "?" + hr.uri;		
							}
														
							hit.Type = hr.resourceType;
							hit.MimeType = hr.mimeType;
							hit.Source = "Network";			//hit.Source = hr.source;
							hit.Score = hr.score;
							
							if (hr.properties.Length  > 0)
							foreach (HitProperty hp in hr.properties) {
							
								Property p 		= Property.New(hp.PKey, hp.PVal);
								p.IsMutable 	= hp.IsMutable;
								p.IsSearched 	= hp.IsSearched;
	
								hit.AddProperty(p);
							}
				
							//Add Snippet					
							((NetworkHit)hit).snippet = hr.snippet;
								
							//if (hr.snippet != null) 
								//log.Debug("\nNBH: URI" + i + "=" + hr.uri + "\n     Snippet=" + hr.snippet);
										
							((NetworkHit)hit).context = new NetContext(hr.hashCode);
							  						
							//Add NetBeagleQueryable instance
							hit.SourceObject = iq;
							hit.SourceObjectName = ((NetworkedBeagle)iq).Name; 
						
							nwhits.Add(hit); 

							count++;							
						}
						catch (Exception ex2) {
		 	
							log.Warn ("Exception in NetBeagleHandler: DoQueryResponseHandler() while processing NetworkHit: {0} from {1}\n Reason: {2} ", hres[i].uri, wsp.Hostname + ":" + wsp.Port, ex2.Message);
							//log.Error ("Exception StackTrace: " + ex.StackTrace);
		 				}				
					}  //end for 

					if (nwhits.Count > 0) 
						result.Add (nwhits);
/*				
					if ((! result.Add (nwhits)) && (! hitRejectsLogged)) 
					{
						hitRejectsLogged = true;
						log.Info("NetBeagleHandler: Network Hits rejected by HitRegulator. Too many Hits!");
					}
*/					
					log.Info("NetBeagleHandler: DoQueryResponseHandler() Got {0} result(s) from Index {1} from Networked Beagle at {2}", count, resp.firstResultIndex, wsp.Hostname + ":" + wsp.Port); 		   		
			
					int index = resp.firstResultIndex + resp.numResults;			
					if (index  < resp.totalResults) {
					
						log.Debug("NetBeagleHandler: DoQueryResponseHandler() invoking GetResults with index: " + index);
						
						string searchToken = resp.searchToken;									
						
						GetResultsRequest req = new GetResultsRequest();
						req.startIndex = index;
						req.searchToken = searchToken;						
											
						IAsyncResult ar2;					
						ar2 = wsp.BeginGetResults(req, NetBeagleHandler.DoQueryResponseHandler, rc);						
						
						return;						
					}										
				} //end if
				else {
						if (resp == null)
							log.Warn("NetBeagleHandler: DoQueryResponseHandler() got Null response from EndBeagleQuery() !");
				}					
		 	}
		 	catch (Exception ex) {
		 	
				log.Error ("Exception in NetBeagleHandler: DoQueryResponseHandler() - {0} - for {1} ", ex.Message, wsp.Hostname + ":" + wsp.Port);
		 	}
		 	
		 	//Signal completion of request handling						
			rc.RequestProcessed = true; 
    	}		
    }

	public class ReqContext {
					
		private IQueryable iq;
		private IQueryResult result;
		private BeagleWebService wsp;
		
		private bool reqProcessed = false;
		private string token = null;
		
		public ReqContext(BeagleWebService wsp, IQueryResult result, IQueryable iq)
		{
			this.wsp = wsp;
			this.result = result;
			this.iq = iq;
			this.reqProcessed = false;
		}
		
		public BeagleWebService GetProxy {
			get { return wsp; }
		}
		
		public IQueryResult GetResult {
			get { return result; }
		}		
	
		public IQueryable GetQueryable {
			get { return iq; }
		}	
		
		public bool RequestProcessed {
			get { return reqProcessed; }
			set { reqProcessed = value; }
		}
		
		public string SearchToken {
			get {return token; }
			set {token = value; }
		}		
	}


	public class NetContext {

		private int _hashCode;
					
		public NetContext(int hashCode)
		{
			this._hashCode = hashCode;
		}		
		
		public int hashCode {
			get { return _hashCode; }
		}			
	}
	
}
