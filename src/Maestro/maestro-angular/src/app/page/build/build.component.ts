import { Component, OnInit, OnChanges } from "@angular/core";
import { ActivatedRoute } from "@angular/router";
import { prettyRepository } from "src/app/util/names";
import { map, shareReplay, switchMap, filter, distinctUntilChanged, tap } from 'rxjs/operators';
import { isAfter, compareAsc, parseISO } from "date-fns";

import { BuildGraph, Build } from 'src/maestro-client/models';
import { Observable, of, timer, OperatorFunction } from 'rxjs';
import { BuildStatusService } from 'src/app/services/build-status.service';
import { BuildStatus } from 'src/app/model/build-status';
import { statefulSwitchMap, StatefulResult, statefulPipe } from 'src/stateful';
import { getCommitLink, getBuildLink, tapLog } from 'src/helpers';
import { BuildService } from 'src/app/services/build.service';
import { trigger, transition, style, animate } from '@angular/animations';
import { Loading } from 'src/stateful/helpers';

interface AzDevBuildInfo {
  isMostRecent: boolean;
  mostRecentFailureLink?: string;
}

const elementOutStyle = style({
  transform: 'translate(100%, 0)',
});

const elementInStyle = style({
  transform: 'translate(0, 0)',
});

@Component({
  selector: "mc-build",
  templateUrl: "./build.component.html",
  styleUrls: ["./build.component.scss"],
  animations: [
    trigger("toast", [
      transition(":enter", [
        elementOutStyle,
        animate("0.5s ease-out", elementInStyle),
      ]),
      transition(":leave", [
        elementInStyle,
        animate("0.5s ease-in", elementOutStyle),
      ]),
    ]),
  ],
})
export class BuildComponent implements OnInit, OnChanges {
  public repositoryDisplay = prettyRepository;

  public constructor(private route: ActivatedRoute, private buildService: BuildService, private buildStatusService: BuildStatusService) { }

  public graph$!: Observable<StatefulResult<BuildGraph>>;
  public build$!: Observable<StatefulResult<Build>>;
  public azDevBuildInfo$!: Observable<StatefulResult<AzDevBuildInfo>>;

  public includeToolsets: boolean = false;

  public neverToastNewBuilds: boolean = false;

  public toastVisible: boolean = false;
  public toastDate?: Date;
  public acceptToast?: () => void;

  private toastNewBuild(): OperatorFunction<number,number> {
    const self = this;
    let haveBuild = false;
    return function(source: Observable<number>) {
      return new Observable<number>(observer => {
        const sourceSub = source.subscribe({
          next(buildId) {
            if (!haveBuild || self.neverToastNewBuilds) {
              haveBuild = true;
              observer.next(buildId);
              return;
            }
            console.log("Toasting Latest Build: ", buildId);
            self.toastVisible = true;
            self.toastDate = new Date();
            self.acceptToast = () => {
              console.log("Accepting Latest Build: ", buildId);
              self.toastVisible = false;
              observer.next(buildId);
            };
          },
          error(err) {
            observer.error(err);
          },
          complete() {
            observer.complete();
          }
        });

        return () => sourceSub.unsubscribe();
      });
    }
  }

  public ngOnInit() {
    const buildId$ = this.route.paramMap.pipe(
      map(params => {
        const buildId = params.get("buildId");
        const channelId = params.get("channelId");
        const repository = params.get("repository");
        if (buildId == null) {
          throw new Error("buildId was null");
        }
        if (channelId == null) {
          throw new Error("channelId was null");
        }
        if (repository == null) {
          throw new Error("repository was null");
        }
        return {buildId, channelId, repository};
      }),
      tap(v => {
        console.log("Params: ", v);
        this.toastVisible = false;
      }),
      switchMap(params => {
        if (params.buildId == "latest") {
          return this.buildService.getLatestBuildId(+params.channelId, params.repository).pipe(
            statefulPipe(
              this.toastNewBuild(),
            ),
          );
        }
        else {
          return of(+params.buildId);
        }
      }),
      tapLog("Showing Latest:"),
      shareReplay({
        bufferSize: 1,
        refCount: true,
      }),
    );
    this.build$ = buildId$.pipe(
      statefulPipe(
        switchMap(id => this.buildService.getBuild(id)),
      ),
    );
    this.graph$ = buildId$.pipe(
      statefulPipe(
        statefulSwitchMap((id) => {
          return this.buildService.getBuildGraph(id);
        }),
      ),
    );


    const reloadInterval = 1000 * 60 * 5;
    let emittedLoading = false;
    this.azDevBuildInfo$ = this.build$.pipe(
      statefulPipe(
        switchMap(b => {
          return timer(0, reloadInterval).pipe(
            map(() => b),
          );
        }),
        tap(() => console.log("getting azdev info")),
        statefulSwitchMap(b => this.getBuildInfo(b)),
        filter(r => {
          if (!(r instanceof Loading)) {
            return true;
          }
          // emit only the first "Loading" instance so refreshes don't cause the loading spinner to show up
          if (!emittedLoading)  {
            emittedLoading = true;
            return true;
          }
          return false;
        }),
      ),
    );
  }

  public ngOnChanges() {
  }

  public haveAzDevInfo(build: Build): boolean {
    return !!build.azureDevOpsAccount &&
           !!build.azureDevOpsProject &&
           !!build.azureDevOpsBuildDefinitionId &&
           !!build.azureDevOpsBranch;
  }


  public getBuildInfo(build: Build): Observable<AzDevBuildInfo> {
    if (!build.azureDevOpsAccount) {
      throw new Error("azureDevOpsAccount undefined");
    }
    if (!build.azureDevOpsProject) {
      throw new Error("azureDevOpsProject undefined");
    }
    if (!build.azureDevOpsBuildDefinitionId) {
      throw new Error("azureDevOpsBuildDefinitionId undefined");
    }
    if (!build.azureDevOpsBranch) {
      throw new Error("azureDevOpsBranch undefined");
    }
    return this.buildStatusService.getBranchStatus(build.azureDevOpsAccount, build.azureDevOpsProject, build.azureDevOpsBuildDefinitionId, build.azureDevOpsBranch, 5)
      .pipe(
        map(builds => {
          function isNewer(b: BuildStatus): boolean {
            if (b.status === "inProgress") {
              return false;
            }
            if (b.id === build.azureDevOpsBuildId) {
              return false;
            }
            return isAfter(parseISO(b.finishTime), build.dateProduced);
          }

          let isMostRecent: boolean;
          let mostRecentFailureLink: string | undefined;

          const newerBuilds = builds.value.filter(isNewer).sort((l, r) => compareAsc(parseISO(l.finishTime), parseISO(r.finishTime)));
          if (!newerBuilds.length) {
            isMostRecent = true;
            mostRecentFailureLink = undefined;
          } else {
            isMostRecent = false;
            const recentFailure = newerBuilds.find(b => b.result == "failed");
            if (recentFailure) {
              mostRecentFailureLink = this.getBuildLinkFromAzdo(build.azureDevOpsAccount as string, build.azureDevOpsProject as string, recentFailure.id);
            } else {
              mostRecentFailureLink = undefined;
            }
          }

          console.log(`Determined isMostRecent:${isMostRecent}, mostRecentFailureLink:${mostRecentFailureLink}`);
          return {
            isMostRecent,
            mostRecentFailureLink,
          };
        }),
      );
  }

  public getCommitLink = getCommitLink;

  public getBuildLink = getBuildLink;

  public getRepo(build: Build) {
    return build.gitHubRepository || build.azureDevOpsRepository;
  }

  public getBuildLinkFromAzdo(account: string, project: string, buildId: number): string {
    return `https://dev.azure.com` +
      `/${account}` +
      `/${project}` +
      `/_build/results` +
      `?view=results&buildId=${buildId}`;
  }
}